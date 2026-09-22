namespace AnteraApp.Api.Models
{
    public class AuthSession
    {
        public string AccessToken { get; set; } = null!;
        public string? RefreshToken { get; set; }
    }
}
