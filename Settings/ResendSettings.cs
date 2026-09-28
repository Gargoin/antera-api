namespace AnteraApp.Api.Settings;

public class ResendSettings
{
    public const string SectionName = "Resend";

    public string ApiKey { get; set; } = string.Empty;
    public string TemplateId { get; set; } = string.Empty;
    public string AccountDeletionTemplateId { get; set; } = string.Empty;
    public string PasswordResetTemplateId { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string FrontendBaseUrl { get; set; } = string.Empty;
}
