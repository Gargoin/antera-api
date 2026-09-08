namespace AnteraApp.Api.Models;

public sealed record LocationSuggestionDto(
    string Name,
    string? Province,
    double Latitude,
    double Longitude);
