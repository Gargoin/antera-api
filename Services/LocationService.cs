using Microsoft.Extensions.Caching.Memory;
using System.Globalization;
using System.Text.Json;

namespace AnteraApp.Api.Services;

public sealed class LocationService(HttpClient httpClient, IMemoryCache cache)
{
    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static DateTimeOffset _nextRequestAt = DateTimeOffset.MinValue;

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

public sealed record LocationContext(string DisplayName, string? Region)
{
    public static readonly LocationContext Approximate = new("Ubicación aproximada", null);
}
