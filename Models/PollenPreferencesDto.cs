namespace AnteraApp.Api.Models;

public sealed record PollenTypeDto(
    string Id,
    string Name,
    IReadOnlyList<string> Aliases);

public sealed record PollenPreferencesResponse(
    IReadOnlyList<PollenTypeDto> PollenTypes);

public sealed record UpdatePollenPreferencesRequest(
    IReadOnlyList<string>? PollenTypeIds);
