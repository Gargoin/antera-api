using AnteraApp.Api.Settings;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AnteraApp.Api.Services;

public sealed class ResendEmailService
{
    private readonly HttpClient _client;
    private readonly ResendSettings _settings;
    private readonly ILogger<ResendEmailService> _logger;

    public ResendEmailService(
        HttpClient client,
        IOptions<ResendSettings> settings,
        ILogger<ResendEmailService> logger)
    {
        _client = client;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task SendConfirmationAsync(string recipient, string name, string token, CancellationToken cancellationToken)
    {
        var frontendUrl = GetFrontendUrl();

        var confirmationUrl = new UriBuilder(frontendUrl)
        {
            Path = "/confirmar-correo",
            Query = $"token={Uri.EscapeDataString(token)}"
        }.Uri.ToString();

        await SendTemplateAsync(
            _settings.TemplateId,
            recipient,
            new { USER_NAME = name, CONFIRMATION_URL = confirmationUrl },
            "confirmación",
            cancellationToken);
    }

    public Task SendAccountDeletionAsync(string recipient, string name, CancellationToken cancellationToken) =>
        SendTemplateAsync(
            _settings.AccountDeletionTemplateId,
            recipient,
            new { USER_NAME = name },
            "despedida",
            cancellationToken);

    public async Task SendPasswordResetAsync(string recipient, string name, string token, CancellationToken cancellationToken)
    {
        var frontendUrl = GetFrontendUrl();
        var passwordResetUrl = new UriBuilder(frontendUrl)
        {
            Path = "/restablecer-contrasena",
            Query = $"token={Uri.EscapeDataString(token)}"
        }.Uri.ToString();

        await SendTemplateAsync(
            _settings.PasswordResetTemplateId,
            recipient,
            new { USER_NAME = name, PASSWORD_RESET_URL = passwordResetUrl },
            "restablecimiento de contraseña",
            cancellationToken);
    }

    private Uri GetFrontendUrl()
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey) ||
            string.IsNullOrWhiteSpace(_settings.From) ||
            !Uri.TryCreate(_settings.FrontendBaseUrl, UriKind.Absolute, out var frontendUrl))
        {
            throw new InvalidOperationException("El envío de correo aún no está configurado.");
        }

        return frontendUrl;
    }

    private async Task SendTemplateAsync(
        string templateId,
        string recipient,
        object variables,
        string emailKind,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey) ||
            string.IsNullOrWhiteSpace(_settings.From) ||
            string.IsNullOrWhiteSpace(templateId))
        {
            throw new InvalidOperationException("El envío de correo aún no está configurado.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "emails")
        {
            Content = JsonContent.Create(
                new
                {
                    from = _settings.From,
                    to = new[] { recipient },
                    template = new { id = templateId, variables }
                },
                options: new JsonSerializerOptions { PropertyNamingPolicy = null })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);

        using var response = await _client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "Resend rechazó el correo de {EmailKind} con código {StatusCode}: {ResponseBody}",
                emailKind,
                (int)response.StatusCode,
                responseBody);
            throw new InvalidOperationException($"No se ha podido enviar el correo de {emailKind}.");
        }
    }
}
