namespace AnteraApp.Api.Models;

public sealed record PasswordUpdateResult(bool Succeeded, string? RefreshToken)
{
    public static readonly PasswordUpdateResult InvalidCurrentPassword = new(false, null);
}
