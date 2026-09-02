using AnteraApp.Api.Models;
using System.Globalization;
using System.Text.Json;

namespace AnteraApp.Api.Services;

public sealed class MadridPollenService(HttpClient httpClient)
{
    private const string ResourceId = "1f2c4851-b69b-4daa-85ae-89f56cabc67d";

    public async Task<RegionalPollenResult> GetCurrentAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            $"api/3/action/datastore_search?resource_id={ResourceId}&limit=10000&sort=fecha_lectura%20desc",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var records = document.RootElement
            .GetProperty("result")
            .GetProperty("records")
            .EnumerateArray()
            .ToArray();

        var newestDate = records
            .Select(record => TryReadDate(record, "fecha_lectura"))
            .Where(date => date.HasValue)
            .Select(date => date!.Value.Date)
            .DefaultIfEmpty()
            .Max();

        if (newestDate == default)
        {
            throw new InvalidOperationException("Madrid pollen data did not include a reading date.");
        }

        var latestRecords = records.Where(record => TryReadDate(record, "fecha_lectura")?.Date == newestDate);
        var species = latestRecords
            .Where(record => TryGetString(record, "tipo_polinico", out _) && TryGetDouble(record, "granos_de_polen_x_metro_cubico", out _))
            .GroupBy(record => GetRequiredString(record, "tipo_polinico"), StringComparer.OrdinalIgnoreCase)
            .Select(group => new PollenSpeciesDto(
                ToDisplayName(group.Key),
                group.Average(record => GetRequiredDouble(record, "granos_de_polen_x_metro_cubico")),
                "granos/m³"))
            .OrderByDescending(item => item.Value)
            .ToArray();

        if (species.Length == 0)
        {
            throw new InvalidOperationException("Madrid pollen data did not include pollen readings.");
        }

        return new RegionalPollenResult(newestDate, species, "Red PALINOCAM");
    }

    private static DateTime? TryReadDate(JsonElement record, string name) =>
        TryGetString(record, name, out var value) &&
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date)
            ? date
            : null;

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetDouble(JsonElement element, string name, out double value)
    {
        value = default;
        return element.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out value);
    }

    private static string GetRequiredString(JsonElement element, string name) =>
        TryGetString(element, name, out var value) ? value : throw new InvalidOperationException();

    private static double GetRequiredDouble(JsonElement element, string name) =>
        TryGetDouble(element, name, out var value) ? value : throw new InvalidOperationException();

    private static string ToDisplayName(string name) => CultureInfo.GetCultureInfo("es-ES").TextInfo.ToTitleCase(name.ToLowerInvariant());
}

public sealed record RegionalPollenResult(
    DateTime Date,
    IReadOnlyList<PollenSpeciesDto> Species,
    string Station);
