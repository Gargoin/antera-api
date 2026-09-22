namespace AnteraApp.Api.Models;

public sealed record UserProfileResponse(string Name, string Email);

public sealed record UpdateUserProfileRequest(string? Name);

public sealed record UpdateUserEmailRequest(string? Email, string? CurrentPassword);

public sealed record UpdateUserPasswordRequest(string? CurrentPassword, string? NewPassword);
