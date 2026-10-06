namespace AnteraApp.Api.Models;

public enum EmailChangeRequestFailure
{
    None,
    InvalidCurrentPassword,
    EmailUnavailable,
    Cooldown
}

public sealed record EmailChangeRequestResult(
    EmailConfirmation? Confirmation,
    EmailChangeRequestFailure Failure);
