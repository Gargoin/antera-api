namespace AnteraApp.Api.Settings
{
    public class MongoDBSettings
    {
        public const string SectionName = "MongoDB";

        public string ConnectionString { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;
    }
}
