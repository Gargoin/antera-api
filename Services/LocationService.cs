using Microsoft.Extensions.Caching.Memory;
using AnteraApp.Api.Models;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AnteraApp.Api.Services;

public sealed class LocationService(HttpClient httpClient, IHttpClientFactory httpClientFactory, IMemoryCache cache)
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
    private static readonly HashSet<string> LocationTypes =
        ["municipio", "poblacion", "toponimo", "comunidad autonoma", "provincia", "isla"];
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
        if (curatedSuggestions.Length > 0)
        {
            return curatedSuggestions;
        }

        var cacheKey = $"location-search:{normalizedQuery.ToUpperInvariant()}";

        var suggestions = await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(12);
            return await ResolveSpanishSettlementsAsync(normalizedQuery, cancellationToken);
        });

        return suggestions ?? [];
    }

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
        var requestUri = $"candidates?q={Uri.EscapeDataString(searchQuery)}&limit=10";

        using var autocompleteClient = httpClientFactory.CreateClient("SpanishLocationAutocomplete");
        using var response = await autocompleteClient.GetAsync(requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var candidates = document.RootElement
            .EnumerateArray()
            .Select(ToLocationCandidate)
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .Where(candidate => IsRelevantToQuery(candidate.Name, comparisonQuery))
            .GroupBy(candidate => NormalizeSearchTerm(candidate.Name))
            .Select(group => group.First())
            .OrderByDescending(candidate => NormalizeSearchTerm(candidate.Name).StartsWith(comparisonQuery, StringComparison.Ordinal))
            .ThenBy(candidate => candidate.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(6)
            .ToArray();

        var suggestions = new List<LocationSuggestionDto>(candidates.Length);
        foreach (var candidate in candidates)
        {
            var suggestion = await ResolveCandidateAsync(autocompleteClient, candidate, cancellationToken);
            if (suggestion is not null)
            {
                suggestions.Add(suggestion);
            }
        }

        return suggestions;
    }

    private static LocationCandidate? ToLocationCandidate(JsonElement candidate)
    {
        var type = GetFirstValue(candidate, "type");
        var id = GetFirstValue(candidate, "id");
        if (string.IsNullOrWhiteSpace(type) ||
            string.IsNullOrWhiteSpace(id) ||
            !LocationTypes.Contains(NormalizeSearchTerm(type.Replace('_', ' '))))
        {
            return null;
        }

        var settlement = GetFirstValue(candidate, "muni", "poblacion", "address");
        if (string.IsNullOrWhiteSpace(settlement))
        {
            return null;
        }

        var region = GetFirstValue(candidate, "comunidadAutonoma", "province");
        var province = string.IsNullOrWhiteSpace(region) ||
                       string.Equals(settlement, region, StringComparison.OrdinalIgnoreCase)
            ? null
            : region;
        return new LocationCandidate(id, type, settlement, province);
    }

    private static async Task<LocationSuggestionDto?> ResolveCandidateAsync(
        HttpClient autocompleteClient,
        LocationCandidate candidate,
        CancellationToken cancellationToken)
    {
        var requestUri = $"find?type={Uri.EscapeDataString(candidate.Type)}&id={Uri.EscapeDataString(candidate.Id)}";
        using var response = await autocompleteClient.GetAsync(requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var result = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().FirstOrDefault()
            : document.RootElement;

        return TryGetCoordinateProperty(result, "lat", out var latitude) &&
               TryGetCoordinateProperty(result, "lng", out var longitude)
            ? new LocationSuggestionDto(candidate.Name, candidate.Province, latitude, longitude)
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
        if (normalizedQuery is "islas baleares" or "illes balears" or "baleares")
        {
            return normalizedName.Contains("baleares", StringComparison.Ordinal);
        }

        return normalizedName.StartsWith(normalizedQuery, StringComparison.Ordinal) ||
               normalizedName.Contains($" {normalizedQuery}", StringComparison.Ordinal);
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

internal sealed record LocationCandidate(string Id, string Type, string Name, string? Province);

public sealed record LocationContext(string DisplayName, string? Region)
{
    public static readonly LocationContext Approximate = new("Ubicación aproximada", null);
}
