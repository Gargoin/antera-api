using AnteraApp.Api.Models;
using System.Xml.Linq;

namespace AnteraApp.Api.Services;

public sealed class CataloniaPollenService(HttpClient httpClient)
{
    private static readonly (string Name, double Latitude, double Longitude)[] Stations =
    [
        ("barcelona", 41.393728, 2.164922),
        ("bellaterra", 41.5000, 2.1060),
        ("girona", 41.9794, 2.8214),
        ("lleida", 41.6176, 0.6200),
        ("manresa", 41.7282, 1.8230),
        ("roquetes", 40.8210, 0.5020),
        ("tarragona", 41.1189, 1.2445),
        ("vielha", 42.7010, 0.7950),
        ("son", 42.6240, 1.0430)
    ];

    public async Task<RegionalPollenResult> GetCurrentAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken)
    {
        var station = Stations.MinBy(candidate =>
            Math.Pow(candidate.Latitude - latitude, 2) + Math.Pow(candidate.Longitude - longitude, 2));
        using var response = await httpClient.GetAsync(
            $"api/v0/forecast/{station.Name}/es/xml",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var document = XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var report = document.Root?.Element("report")
            ?? throw new InvalidOperationException("Catalonia pollen data did not include a report.");
        var names = document.Root?
            .Element("taxons")?
            .Element("pollens")?
            .Elements()
            .ToDictionary(item => item.Name.LocalName, item => (string?)item.Attribute("es") ?? item.Value)
            ?? [];
        var levels = new[] { "Nulo", "Bajo", "Medio", "Alto", "Máximo" };
        var species = report
            .Element("current")?
            .Element("pollens")?
            .Elements()
            .Select(item => int.TryParse(item.Value, out var value) && value > 0
                ? new PollenSpeciesDto(
                    names.GetValueOrDefault(item.Name.LocalName, item.Name.LocalName),
                    value,
                    "nivel regional",
                    levels[Math.Min(value, levels.Length - 1)])
                : null)
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderByDescending(item => item.Value)
            .ToArray() ?? [];

        if (species.Length == 0)
        {
            throw new InvalidOperationException("Catalonia pollen data did not include current levels.");
        }

        var date = DateTime.TryParse(report.Element("date")?.Element("start")?.Value, out var parsed)
            ? parsed
            : DateTime.Today;
        var stationName = report.Element("station")?.Element("name")?.Value ?? station.Name;
        return new RegionalPollenResult(date, species, $"Xarxa Aerobiològica de Catalunya — {stationName}");
    }
}
