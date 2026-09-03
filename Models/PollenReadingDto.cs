namespace AnteraApp.Api.Models;

public sealed record PollenReadingDto(
    DateTime Date,
    string TimeZone,
    string Location,
    double Latitude,
    double Longitude,
    IReadOnlyList<PollenSpeciesDto> Species,
    string Source,
    string ReadingType,
    string? Station,
    string? Notice);

public sealed record PollenSpeciesDto(
    string Name,
    double? Value,
    string Unit,
    string? Level = null,
    string? AllergyLevel = null);
