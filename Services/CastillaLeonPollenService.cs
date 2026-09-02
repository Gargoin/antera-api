using AnteraApp.Api.Models;
using System.Globalization;
using System.Text.Json;

namespace AnteraApp.Api.Services;

public sealed class CastillaLeonPollenService(HttpClient httpClient)
{
    public async Task<RegionalPollenResult> GetCurrentAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            "api/explore/v2.1/catalog/datasets/informacion-polinica-actual/records?limit=100",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var records = document.RootElement.GetProperty("results").EnumerateArray().ToArray();

        var species = records
            .Select(record => ToSpecies(record))
            .Where(item => item is not null)
            .Select(item => item!)
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(item => item.Value ?? 0)
            .ToArray();

        if (species.Length == 0)
        {
            throw new InvalidOperationException("Castilla y León pollen data did not include readings.");
        }

        var date = records
            .Select(record => GetString(record, "fecha", "fecha_lectura", "date"))
            .Select(value => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed) ? parsed : (DateTime?)null)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty(DateTime.Today)
            .Max();

        return new RegionalPollenResult(date, species, "Red Palinológica de Castilla y León");
    }

    private static PollenSpeciesDto? ToSpecies(JsonElement record)
    {
        var name = GetString(record, "tipos_polinicos", "tipo_polinico", "pollen_type", "tipo");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var rawValue = GetString(
            record,
            "granos_de_polen_x_metro_cubico",
            "concentracion",
            "medicion",
            "valor",
            "nivel_actual",
            "nivel_polen",
            "nivel",
            "prevision");
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        return double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? new PollenSpeciesDto(name, value, "granos/m³")
            : new PollenSpeciesDto(name, null, "nivel", rawValue);
    }

    private static string? GetString(JsonElement record, params string[] names)
    {
        foreach (var name in names)
        {
            var property = record.EnumerateObject()
                .FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            if (property.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                continue;
            }

            var value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
