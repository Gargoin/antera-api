namespace AnteraApp.Api.Models;

public sealed record EmailConfirmation(string Recipient, string Name, string Token);
