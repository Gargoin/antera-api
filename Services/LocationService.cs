using AnteraApp.Api.Models;
using AnteraApp.Api.Settings;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AnteraApp.Api.Services;

public sealed class LocationService(
    HttpClient geoapifyClient,
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    IOptions<GeoapifySettings> geoapifySettings)
{
    private readonly GeoapifySettings _geoapifySettings = geoapifySettings.Value;

    private static readonly HashSet<string> SearchStopWords =
        ["de", "del", "la", "las", "el", "los", "y"];

    // Las islas no siempre se catalogan como municipio por los geocodificadores.
    // Sus coordenadas son puntos de referencia para obtener la lectura ambiental de la isla.
    private static readonly IReadOnlyList<LocationSuggestionDto> BalearicIslandSuggestions =
    [
        new("Mallorca", "Islas Baleares", 39.7104, 2.9951),
        new("Menorca", "Islas Baleares", 39.9496, 4.1104),
        new("Ibiza", "Islas Baleares", 38.9800, 1.4300),
        new("Formentera", "Islas Baleares", 38.6950, 1.4530),
        new("Islas Baleares", null, 39.5500, 2.9000)
    ];

    public async Task<string> GetNearestSettlementAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken)
    {
        var context = await GetLocationContextAsync(latitude, longitude, cancellationToken);
        return context.DisplayName;
    }

    public async Task<LocationContext> GetLocationContextAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken)
    {
        var cacheKey = string.Create(CultureInfo.InvariantCulture,
            $"location-context:{Math.Round(latitude, 3)}:{Math.Round(longitude, 3)}");
        if (cache.TryGetValue<LocationContext>(cacheKey, out var cachedContext) && cachedContext is not null)
        {
            return cachedContext;
        }

        var context = await ResolveLocationContextAsync(latitude, longitude, cancellationToken);
        // Una respuesta sin localidad puede ser transitoria. No la retenemos durante horas.
        if (!string.Equals(context.DisplayName, LocationContext.Approximate.DisplayName, StringComparison.Ordinal))
        {
            cache.Set(cacheKey, context, new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(TimeSpan.FromHours(12))
                .SetSize(1));
        }

        return context;
    }

    public async Task<IReadOnlyList<LocationSuggestionDto>> SearchSpanishSettlementsAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var normalizedQuery = query.Trim();
        var normalizedSearchTerm = NormalizeSearchTerm(normalizedQuery);
        var curatedSuggestions = BalearicIslandSuggestions
            .Where(suggestion => IsRelevantToQuery(suggestion.Name, normalizedSearchTerm))
            .ToArray();

        if (curatedSuggestions.Any(suggestion =>
                string.Equals(NormalizeSearchTerm(suggestion.Name), normalizedSearchTerm, StringComparison.Ordinal)))
        {
            return curatedSuggestions;
        }

        var cacheKey = $"location-search:v9:{normalizedQuery.ToUpperInvariant()}";
        if (cache.TryGetValue<IReadOnlyList<LocationSuggestionDto>>(cacheKey, out var cachedSuggestions) &&
            cachedSuggestions is not null)
        {
            return cachedSuggestions;
        }

        // Open-Meteo resuelve por prefijo y no depende de Nominatim, cuya cuota pública no
        // permite completar búsquedas interactivas. Las sugerencias especiales se conservan.
        var suggestions = (await ResolveOpenMeteoSpanishSettlementsAsync(normalizedQuery, cancellationToken))
            .Concat(curatedSuggestions)
            .GroupBy(suggestion => NormalizeSearchTerm(suggestion.Name))
            .Select(group => group.First())
            .Take(6)
            .ToArray();
        cache.Set(cacheKey, suggestions, new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(suggestions.Length > 0 ? TimeSpan.FromHours(12) : TimeSpan.FromMinutes(2))
            .SetSize(1));

        return suggestions;
    }

    private async Task<IReadOnlyList<LocationSuggestionDto>> ResolveOpenMeteoSpanishSettlementsAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("OpenMeteoGeocoding");
        var requestUri = $"v1/search?name={Uri.EscapeDataString(query)}&count=20&language=es&format=json&countryCode=ES";
        using var response = await client.GetAsync(requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var normalizedQuery = NormalizeSearchTerm(query);
        return results
            .EnumerateArray()
            .Where(result => string.Equals(GetFirstValue(result, "country_code"), "ES", StringComparison.OrdinalIgnoreCase))
            .Where(IsSettlementResult)
            .Select(ToOpenMeteoSuggestion)
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .Where(candidate => IsRelevantToQuery(candidate.Location.Name, normalizedQuery))
            .GroupBy(candidate => NormalizeSearchTerm(candidate.Location.Name))
            .Select(group => group.OrderByDescending(candidate => candidate.Population).First())
            .OrderByDescending(candidate => string.Equals(
                NormalizeSearchTerm(candidate.Location.Name), normalizedQuery, StringComparison.Ordinal))
            .ThenByDescending(candidate => NormalizeSearchTerm(candidate.Location.Name)
                .StartsWith(normalizedQuery, StringComparison.Ordinal))
            .ThenByDescending(candidate => candidate.Population)
            .ThenBy(candidate => candidate.Location.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(6)
            .Select(candidate => candidate.Location)
            .ToArray();
    }

    private static bool IsSettlementResult(JsonElement result)
    {
        var featureCode = GetFirstValue(result, "feature_code");
        return featureCode is not null &&
               (featureCode.StartsWith("PPL", StringComparison.OrdinalIgnoreCase) ||
                featureCode is "ADM2" or "ADM3" or "ADM4");
    }

    private static OpenMeteoSuggestion? ToOpenMeteoSuggestion(JsonElement result)
    {
        var name = NormalizeSpanishPlaceNameParticles(GetFirstValue(result, "name"));
        if (string.IsNullOrWhiteSpace(name) ||
            !TryGetCoordinateProperty(result, "latitude", out var latitude) ||
            !TryGetCoordinateProperty(result, "longitude", out var longitude))
        {
            return null;
        }

        var province = NormalizeSpanishPlaceNameParticles(NormalizeProvince(GetFirstValue(result, "admin2", "admin1")));
        return new OpenMeteoSuggestion(
            new LocationSuggestionDto(name, province, latitude, longitude),
            GetPopulation(result));
    }

    private async Task<LocationContext> ResolveLocationContextAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_geoapifySettings.ApiKey))
        {
            return LocationContext.Approximate;
        }

        var requestUri = string.Create(CultureInfo.InvariantCulture,
            $"v1/geocode/reverse?lat={latitude}&lon={longitude}&format=json&lang=es&apiKey={Uri.EscapeDataString(_geoapifySettings.ApiKey)}");
        using var response = await geoapifyClient.GetAsync(requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
        {
            return LocationContext.Approximate;
        }

        var location = results.EnumerateArray().FirstOrDefault();
        if (location.ValueKind != JsonValueKind.Object)
        {
            return LocationContext.Approximate;
        }

        var settlement = NormalizeSpanishPlaceNameParticles(GetFirstValue(location, "city", "town", "village", "municipality", "hamlet", "suburb", "county"));
        var region = NormalizeSpanishPlaceNameParticles(GetFirstValue(location, "state", "province", "region", "county"));
        if (string.IsNullOrWhiteSpace(settlement))
        {
            return new LocationContext("Ubicación aproximada", region);
        }

        var displayName = string.IsNullOrWhiteSpace(region) ||
                          string.Equals(settlement, region, StringComparison.OrdinalIgnoreCase)
            ? settlement
            : $"{settlement}, {region}";
        return new LocationContext(displayName, region);
    }

    private static long GetPopulation(JsonElement result) =>
        result.TryGetProperty("population", out var population) && population.ValueKind == JsonValueKind.Number &&
        population.TryGetInt64(out var value)
            ? value
            : 0;

    private static string? NormalizeProvince(string? value) => value?
        .Replace("Provincia de ", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("Comunidad Autónoma de ", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("Principado de ", string.Empty, StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeSpanishPlaceNameParticles(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 1; index < words.Length; index++)
        {
            if (string.Equals(words[index], "de", StringComparison.OrdinalIgnoreCase))
            {
                words[index] = "de";
            }
        }

        return string.Join(' ', words);
    }

    private static string NormalizeSearchTerm(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                if (char.IsLetterOrDigit(character))
                {
                    builder.Append(character);
                }
                else if (builder.Length > 0 && builder[^1] != ' ')
                {
                    builder.Append(' ');
                }
            }
        }

        return builder.ToString().Trim().ToLowerInvariant();
    }

    private static bool IsRelevantToQuery(string locationName, string normalizedQuery)
    {
        var normalizedName = NormalizeSearchTerm(locationName);
        var relevantTerms = normalizedQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(term => !SearchStopWords.Contains(term))
            .ToArray();
        if (normalizedQuery is "islas baleares" or "illes balears" or "baleares")
        {
            return normalizedName.Contains("baleares", StringComparison.Ordinal);
        }

        return normalizedName.StartsWith(normalizedQuery, StringComparison.Ordinal) ||
               normalizedName.Contains($" {normalizedQuery}", StringComparison.Ordinal) ||
               (relevantTerms.Length > 0 &&
                relevantTerms.All(term => normalizedName.Contains(term, StringComparison.Ordinal)));
    }

    private static bool TryGetCoordinateProperty(JsonElement result, string propertyName, out double coordinate)
    {
        coordinate = default;
        return result.TryGetProperty(propertyName, out var value) && TryGetCoordinate(value, out coordinate);
    }

    private static bool TryGetCoordinate(JsonElement value, out double coordinate)
    {
        coordinate = default;
        return value.ValueKind == JsonValueKind.Number
            ? value.TryGetDouble(out coordinate)
            : value.ValueKind == JsonValueKind.String &&
              double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out coordinate);
    }

    private static string? GetFirstValue(JsonElement address, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (address.TryGetProperty(propertyName, out var property) &&
                property.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(property.GetString()))
            {
                return property.GetString();
            }
        }

        return null;
    }
}

internal sealed record OpenMeteoSuggestion(LocationSuggestionDto Location, long Population);

public sealed record LocationContext(string DisplayName, string? Region)
{
    public static readonly LocationContext Approximate = new("Ubicación aproximada", null);
}
