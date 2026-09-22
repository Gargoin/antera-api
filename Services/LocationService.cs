using Microsoft.Extensions.Caching.Memory;
using AnteraApp.Api.Models;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AnteraApp.Api.Services;

public sealed class LocationService(
    HttpClient httpClient,
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache)
{
    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static DateTimeOffset _nextRequestAt = DateTimeOffset.MinValue;
    private static readonly IReadOnlyDictionary<string, string> SearchAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mallorca"] = "Mallorca, Illes Balears, España",
            ["baleares"] = "Illes Balears, España",
            ["islas baleares"] = "Illes Balears, España",
            ["illes balears"] = "Illes Balears, España"
        };
    private static readonly HashSet<string> SearchStopWords =
        ["de", "del", "la", "las", "el", "los", "y"];
    private static readonly HashSet<string> SettlementPlaceTypes =
        ["city", "town", "village", "hamlet"];
    private static readonly HashSet<string> SettlementAdministrativeTypes =
        ["city", "town", "village", "hamlet", "municipality", "city_district"];
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

        var context = await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(12);
            return await ResolveLocationContextAsync(latitude, longitude, cancellationToken);
        });

        return context ?? LocationContext.Approximate;
    }

    public async Task<IReadOnlyList<LocationSuggestionDto>> SearchSpanishSettlementsAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var normalizedQuery = query.Trim();
        var curatedSuggestions = BalearicIslandSuggestions
            .Where(suggestion => IsRelevantToQuery(suggestion.Name, NormalizeSearchTerm(normalizedQuery)))
            .ToArray();
        // Las islas se mantienen como resultados especiales cuando se buscan por su nombre
        // completo. Una coincidencia parcial ("Mal") no debe impedir que Nominatim aporte
        // otras localidades, como Málaga.
        if (curatedSuggestions.Any(suggestion =>
                string.Equals(NormalizeSearchTerm(suggestion.Name), NormalizeSearchTerm(normalizedQuery), StringComparison.Ordinal)))
        {
            return curatedSuggestions;
        }

        // No se persisten respuestas vacías: el proveedor puede completar su índice más tarde
        // y una búsqueda temporalmente sin candidatos no debe ocultar ubicaciones durante horas.
        var cacheKey = $"location-search:v8:{normalizedQuery.ToUpperInvariant()}";
        if (cache.TryGetValue<IReadOnlyList<LocationSuggestionDto>>(cacheKey, out var cachedSuggestions))
        {
            return cachedSuggestions;
        }

        var autocompleteSuggestions = await ResolveOpenMeteoSpanishSettlementsAsync(
            normalizedQuery, cancellationToken);
        var nominatimSuggestions = autocompleteSuggestions.Count < 6
            ? await ResolveSpanishSettlementsAsync(normalizedQuery, cancellationToken)
            : [];
        var suggestions = autocompleteSuggestions
            .Concat(nominatimSuggestions)
            .Concat(curatedSuggestions)
            .GroupBy(suggestion => NormalizeSearchTerm(suggestion.Name))
            .Select(group => group.First())
            .OrderByDescending(suggestion => NormalizeSearchTerm(suggestion.Name)
                .StartsWith(NormalizeSearchTerm(normalizedQuery), StringComparison.Ordinal))
            .ThenBy(suggestion => suggestion.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(6)
            .ToArray();
        if (suggestions.Length > 0)
        {
            cache.Set(cacheKey, suggestions, TimeSpan.FromHours(12));
        }

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
            .Where(suggestion => suggestion is not null)
            .Select(suggestion => suggestion!)
            .Where(suggestion => IsRelevantToQuery(suggestion.Name, normalizedQuery))
            .GroupBy(suggestion => NormalizeSearchTerm(suggestion.Name))
            .Select(group => group.First())
            .OrderByDescending(suggestion => NormalizeSearchTerm(suggestion.Name)
                .StartsWith(normalizedQuery, StringComparison.Ordinal))
            .ThenBy(suggestion => suggestion.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(6)
            .ToArray();
    }

    private static bool IsSettlementResult(JsonElement result)
    {
        var featureCode = GetFirstValue(result, "feature_code");
        return featureCode is not null &&
               (featureCode.StartsWith("PPL", StringComparison.OrdinalIgnoreCase) ||
                featureCode is "ADM2" or "ADM3" or "ADM4");
    }

    private static LocationSuggestionDto? ToOpenMeteoSuggestion(JsonElement result)
    {
        var name = GetFirstValue(result, "name");
        if (string.IsNullOrWhiteSpace(name) ||
            !TryGetCoordinateProperty(result, "latitude", out var latitude) ||
            !TryGetCoordinateProperty(result, "longitude", out var longitude))
        {
            return null;
        }

        var province = NormalizeProvince(GetFirstValue(result, "admin2", "admin1"));
        return new LocationSuggestionDto(name, province, latitude, longitude);
    }

    private static string? NormalizeProvince(string? value) => value?
        .Replace("Provincia de ", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("Comunidad Autónoma de ", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("Principado de ", string.Empty, StringComparison.OrdinalIgnoreCase);

    private async Task<LocationContext> ResolveLocationContextAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken)
    {
        await RequestGate.WaitAsync(cancellationToken);
        try
        {
            var delay = _nextRequestAt - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            _nextRequestAt = DateTimeOffset.UtcNow.AddSeconds(1);

            var requestUri = string.Create(CultureInfo.InvariantCulture,
                $"reverse?format=jsonv2&addressdetails=1&layer=address&zoom=13&accept-language=es&lat={latitude}&lon={longitude}");
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (!document.RootElement.TryGetProperty("address", out var address))
            {
                return LocationContext.Approximate;
            }

            var settlement = GetFirstValue(address, "village", "town", "city", "municipality", "hamlet", "suburb", "county");
            var region = GetFirstValue(address, "state", "province", "region", "county");

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
        finally
        {
            RequestGate.Release();
        }
    }

    private async Task<IReadOnlyList<LocationSuggestionDto>> ResolveSpanishSettlementsAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var normalizedQuery = NormalizeSearchTerm(query);
        var searchQuery = SearchAliases.TryGetValue(normalizedQuery, out var aliasedQuery)
            ? aliasedQuery
            : query;
        // Se busca con el alias oficial, pero se filtra con lo que escribió la persona.
        // Así "Mallorca" no exige que el resultado incluya también "Illes Balears, España".
        var comparisonQuery = normalizedQuery;
        await RequestGate.WaitAsync(cancellationToken);
        try
        {
            var delay = _nextRequestAt - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            _nextRequestAt = DateTimeOffset.UtcNow.AddSeconds(1);
            var requestUri = $"search?format=jsonv2&addressdetails=1&countrycodes=es&limit=50&accept-language=es&q={Uri.EscapeDataString(searchQuery)}";
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return document.RootElement
                .EnumerateArray()
                .Select(ToSettlementSuggestion)
                .Where(suggestion => suggestion is not null)
                .Select(suggestion => suggestion!)
                .Where(suggestion => IsRelevantToQuery(suggestion.SearchText, comparisonQuery))
                .GroupBy(suggestion => NormalizeSearchTerm(suggestion.Name))
                .Select(group => group.First())
                .OrderByDescending(suggestion => NormalizeSearchTerm(suggestion.Name).StartsWith(comparisonQuery, StringComparison.Ordinal))
                .ThenBy(suggestion => suggestion.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(6)
                .Select(suggestion => new LocationSuggestionDto(
                    suggestion.Name, suggestion.Province, suggestion.Latitude, suggestion.Longitude))
                .ToArray();
        }
        finally
        {
            RequestGate.Release();
        }
    }

    private static SettlementSuggestion? ToSettlementSuggestion(JsonElement result)
    {
        var category = GetFirstValue(result, "category");
        var type = GetFirstValue(result, "type");
        var addressType = GetFirstValue(result, "addresstype");
        var isPlace = string.Equals(category, "place", StringComparison.OrdinalIgnoreCase) &&
                      type is not null && SettlementPlaceTypes.Contains(type);
        var isAdministrativeSettlement =
            string.Equals(category, "boundary", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(type, "administrative", StringComparison.OrdinalIgnoreCase) &&
            addressType is not null && SettlementAdministrativeTypes.Contains(addressType);
        if (!isPlace && !isAdministrativeSettlement)
        {
            return null;
        }

        var settlement = GetFirstValue(result, "name");
        if (string.IsNullOrWhiteSpace(settlement))
        {
            return null;
        }

        if (!result.TryGetProperty("address", out var address))
        {
            return null;
        }

        var municipality = GetFirstValue(address, "city", "town", "village", "municipality", "county");
        var region = GetFirstValue(address, "state", "province", "region", "county");
        var province = string.IsNullOrWhiteSpace(region) ||
                       string.Equals(settlement, region, StringComparison.OrdinalIgnoreCase)
            ? null
            : region;
        var searchText = string.Join(' ', new[] { settlement, municipality }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        return TryGetCoordinateProperty(result, "lat", out var latitude) &&
               TryGetCoordinateProperty(result, "lon", out var longitude)
            ? new SettlementSuggestion(settlement, province, latitude, longitude, searchText)
            : null;
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

internal sealed record SettlementSuggestion(
    string Name,
    string? Province,
    double Latitude,
    double Longitude,
    string SearchText);

public sealed record LocationContext(string DisplayName, string? Region)
{
    public static readonly LocationContext Approximate = new("Ubicación aproximada", null);
}
