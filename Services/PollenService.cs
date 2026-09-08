using AnteraApp.Api.Models;
using System.Globalization;
using System.Text.Json;

namespace AnteraApp.Api.Services;

public sealed class PollenService(
    HttpClient httpClient,
    LocationService locationService,
    MadridPollenService madridPollenService,
    CastillaLeonPollenService castillaLeonPollenService,
    CataloniaPollenService cataloniaPollenService,
    ILogger<PollenService> logger)
{
    private static readonly (string ApiName, string DisplayName)[] PollenTypes =
    [
        ("alder_pollen", "Aliso"),
        ("birch_pollen", "Abedul"),
        ("grass_pollen", "Gramíneas"),
        ("mugwort_pollen", "Artemisa"),
        ("olive_pollen", "Olivo"),
        ("ragweed_pollen", "Ambrosía")
    ];

    public async Task<PollenReadingDto> GetCurrentAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken)
    {
        var locationContext = await TryGetLocationContextAsync(latitude, longitude, cancellationToken);
        if (IsMadrid(locationContext.Region, latitude, longitude))
        {
            try
            {
                var regionalReading = await madridPollenService.GetCurrentAsync(cancellationToken);
                return new PollenReadingDto(
                    regionalReading.Date,
                    "Europe/Madrid",
                    locationContext.DisplayName,
                    latitude,
                    longitude,
                    PollenAllergyLevelClassifier.Classify(PollenCatalog.NormalizeSpecies(regionalReading.Species)),
                    "Comunidad de Madrid — Datos abiertos PALINOCAM",
                    "Medición regional",
                    regionalReading.Station,
                    "Las mediciones proceden de la red regional y pueden publicarse con retraso respecto al momento de consulta.");
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException)
            {
                logger.LogWarning(exception, "No se pudo obtener la lectura regional de PALINOCAM.");
            }
        }

        if (IsCastillaLeon(locationContext.Region))
        {
            try
            {
                var regionalReading = await castillaLeonPollenService.GetCurrentAsync(cancellationToken);
                return new PollenReadingDto(
                    regionalReading.Date,
                    "Europe/Madrid",
                    locationContext.DisplayName,
                    latitude,
                    longitude,
                    PollenAllergyLevelClassifier.Classify(PollenCatalog.NormalizeSpecies(regionalReading.Species)),
                    "Junta de Castilla y León — Datos abiertos",
                    "Información regional",
                    regionalReading.Station,
                    "La red regional publica niveles y previsiones semanalmente; consulta la fecha de actualización antes de tomar decisiones de salud.");
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException)
            {
                // Si la fuente regional no responde, se mantiene la cobertura con el modelo ambiental.
            }
        }

        if (IsCatalonia(locationContext.Region, latitude, longitude))
        {
            try
            {
                var regionalReading = await cataloniaPollenService.GetCurrentAsync(latitude, longitude, cancellationToken);
                return new PollenReadingDto(
                    regionalReading.Date,
                    "Europe/Madrid",
                    locationContext.DisplayName,
                    latitude,
                    longitude,
                    regionalReading.Species,
                    "Xarxa Aerobiològica de Catalunya (XAC)",
                    "Información regional",
                    regionalReading.Station,
                    "Niveles y previsiones semanales de la XAC. Fuente bajo licencia CC BY-NC-SA 4.0.");
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
            {
                logger.LogWarning(exception, "No se pudo obtener la lectura regional de la XAC.");
            }
        }

        var variables = string.Join(',', PollenTypes.Select(type => type.ApiName));
        var requestUri = string.Create(CultureInfo.InvariantCulture,
            $"v1/air-quality?latitude={latitude}&longitude={longitude}&current={variables}&timezone=auto");

        using var response = await httpClient.GetAsync(requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var root = document.RootElement;
        var current = root.GetProperty("current");
        var species = PollenAllergyLevelClassifier.Classify(PollenCatalog.NormalizeSpecies(PollenTypes
            .Where(type => current.TryGetProperty(type.ApiName, out var value) && value.ValueKind == JsonValueKind.Number)
            .Select(type => new PollenSpeciesDto(
                type.DisplayName,
                current.GetProperty(type.ApiName).GetDouble(),
                "granos/m³"))
            .OrderByDescending(type => type.Value)
            .ToArray()));

        var time = current.GetProperty("time").GetString();
        if (!DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new InvalidOperationException("The pollen provider returned an invalid timestamp.");
        }

        var responseLatitude = root.GetProperty("latitude").GetDouble();
        var responseLongitude = root.GetProperty("longitude").GetDouble();
        var location = locationContext.DisplayName;
        var notice = IsCanaryIslands(locationContext.Region)
            ? "Canarias no dispone actualmente de una API regional pública de polen integrada. Se muestra una estimación ambiental para tu zona."
            : "No hay una API regional pública integrada para esta zona. Se muestra una estimación ambiental, no una medición de estación.";

        return new PollenReadingDto(
            date,
            root.GetProperty("timezone").GetString() ?? "UTC",
            location,
            responseLatitude,
            responseLongitude,
            species,
            "Open-Meteo / CAMS",
            "Estimación ambiental",
            null,
            notice);
    }

    private async Task<LocationContext> TryGetLocationContextAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken)
    {
        try
        {
            return await locationService.GetLocationContextAsync(latitude, longitude, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            return LocationContext.Approximate;
        }
    }

    private static bool IsMadrid(string? region, double latitude, double longitude) =>
        string.Equals(region, "Comunidad de Madrid", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(region, "Madrid", StringComparison.OrdinalIgnoreCase) ||
        IsWithinMadridCommunity(latitude, longitude);

    // La geocodificación inversa es una mejora de contexto, no debe decidir si se
    // utiliza una red regional. Este límite cubre la Comunidad de Madrid cuando
    // Nominatim no responde o agota su cuota temporalmente.
    private static bool IsWithinMadridCommunity(double latitude, double longitude) =>
        latitude is >= 40.05 and <= 41.2 && longitude is >= -4.65 and <= -2.95;

    private static bool IsCanaryIslands(string? region) =>
        !string.IsNullOrWhiteSpace(region) &&
        (region.Contains("Canarias", StringComparison.OrdinalIgnoreCase) ||
         region.Contains("Canary", StringComparison.OrdinalIgnoreCase));

    private static bool IsCatalonia(string? region, double latitude, double longitude) =>
        !string.IsNullOrWhiteSpace(region) && region.Contains("Catalu", StringComparison.OrdinalIgnoreCase) ||
        latitude is >= 40.5 and <= 42.9 && longitude is >= 0.1 and <= 3.4;

    private static bool IsCastillaLeon(string? region) =>
        !string.IsNullOrWhiteSpace(region) &&
        region.Contains("Castilla y León", StringComparison.OrdinalIgnoreCase);
}
