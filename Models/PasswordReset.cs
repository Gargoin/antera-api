namespace AnteraApp.Api.Models;

public sealed record PasswordReset(string Recipient, string Name, string Token);

public sealed record DeletedAccount(string Recipient, string Name);
