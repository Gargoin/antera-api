using AnteraApp.Api.Models;
using Microsoft.Extensions.Caching.Memory;
using System.Globalization;
using System.Text.Json;

namespace AnteraApp.Api.Services;

public sealed class AirQualityService(HttpClient httpClient, IMemoryCache cache)
{
    public async Task<AirQualityReadingDto> GetCurrentAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken)
    {
        var cacheKey = string.Create(CultureInfo.InvariantCulture,
            $"air-quality:{Math.Round(latitude, 3)}:{Math.Round(longitude, 3)}");
        var reading = await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            return await GetCurrentUncachedAsync(latitude, longitude, cancellationToken);
        });

        return reading ?? throw new InvalidOperationException("No air quality reading could be generated.");
    }

    private async Task<AirQualityReadingDto> GetCurrentUncachedAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken)
    {
        var requestUri = string.Create(CultureInfo.InvariantCulture,
            $"v1/air-quality?latitude={latitude}&longitude={longitude}&current=european_aqi,pm2_5,pm10,nitrogen_dioxide,ozone&timezone=auto");
        using var response = await httpClient.GetAsync(requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var current = root.GetProperty("current");
        var time = current.GetProperty("time").GetString();
        if (!DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new InvalidOperationException("The air quality provider returned an invalid timestamp.");
        }

        var europeanAqi = GetRequiredNumber(current, "european_aqi");
        return new AirQualityReadingDto(
            date,
            root.GetProperty("timezone").GetString() ?? "UTC",
            europeanAqi,
            ClassifyEuropeanAqi(europeanAqi),
            GetRequiredNumber(current, "pm2_5"),
            GetRequiredNumber(current, "pm10"),
            GetRequiredNumber(current, "nitrogen_dioxide"),
            GetRequiredNumber(current, "ozone"),
            "Open-Meteo / CAMS",
            "Estimación basada en el modelo de calidad del aire de CAMS; no es una medición de estación.");
    }

    private static double GetRequiredNumber(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var value)
            ? value
            : throw new InvalidOperationException($"The air quality provider did not include {name}.");

    private static string ClassifyEuropeanAqi(double value) => value switch
    {
        <= 20 => "Buena",
        <= 40 => "Aceptable",
        <= 60 => "Moderada",
        <= 80 => "Deficiente",
        <= 100 => "Muy deficiente",
        _ => "Extremadamente deficiente"
    };
}
