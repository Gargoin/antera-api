namespace AnteraApp.Api.Models;

public sealed record LocationSuggestionDto(
    string Name,
    double Latitude,
    double Longitude);
