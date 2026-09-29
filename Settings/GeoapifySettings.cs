namespace AnteraApp.Api.Settings;

public sealed class GeoapifySettings
{
    public const string SectionName = "Geoapify";

    public string ApiKey { get; set; } = string.Empty;
}
