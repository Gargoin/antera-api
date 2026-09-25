using AnteraApp.Api.Settings;
using AnteraApp.Api.Services;
using AnteraApp.Api.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "AnteraApp.Api", Version = "v1" });

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Example: 'Bearer {token}'",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });

    c.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer", document)] = []
    });
});

builder.Services.AddOptions<MongoDBSettings>()
    .BindConfiguration(MongoDBSettings.SectionName)
    .Validate(settings => !string.IsNullOrWhiteSpace(settings.ConnectionString),
        "MongoDB:ConnectionString is required. Configure it outside source control.")
    .Validate(settings => !string.IsNullOrWhiteSpace(settings.DatabaseName),
        "MongoDB:DatabaseName is required.")
    .ValidateOnStart();

builder.Services.AddOptions<JwtSettings>()
    .BindConfiguration(JwtSettings.SectionName)
    .Validate(settings => Encoding.UTF8.GetByteCount(settings.Secret) >= 32,
        "Jwt:Secret must contain at least 32 bytes and must be configured outside source control.")
    .Validate(settings => !string.IsNullOrWhiteSpace(settings.Issuer),
        "Jwt:Issuer is required.")
    .Validate(settings => !string.IsNullOrWhiteSpace(settings.Audience),
        "Jwt:Audience is required.")
    .Validate(settings => settings.ExpirationMinutes is >= 5 and <= 60,
        "Jwt:ExpirationMinutes must be between 5 and 60.")
    .Validate(settings => settings.RefreshTokenExpirationDays is >= 1 and <= 90,
        "Jwt:RefreshTokenExpirationDays must be between 1 and 90.")
    .ValidateOnStart();

var jwtSettings = builder.Configuration
    .GetRequiredSection(JwtSettings.SectionName)
    .Get<JwtSettings>()
    ?? throw new InvalidOperationException("JWT configuration is required.");

if (Encoding.UTF8.GetByteCount(jwtSettings.Secret) < 32)
{
    throw new InvalidOperationException(
        "Jwt:Secret must contain at least 32 bytes and must be configured outside source control.");
}

builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<JwtService>();
builder.Services.AddSingleton<PollenPreferencesService>();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<PollenService>(client =>
{
    client.BaseAddress = new Uri("https://air-quality-api.open-meteo.com/");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient<MadridPollenService>(client =>
{
    client.BaseAddress = new Uri("https://datos.comunidad.madrid/");
    client.Timeout = TimeSpan.FromSeconds(5);
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "AnteraApp/1.0 (+https://github.com/antera-dev/antera-app)");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
});
builder.Services.AddHttpClient<CastillaLeonPollenService>(client =>
{
    client.BaseAddress = new Uri("https://analisis.datosabiertos.jcyl.es/");
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddHttpClient<CataloniaPollenService>(client =>
{
    client.BaseAddress = new Uri("https://aerobiologia.cat/");
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddHttpClient<LocationService>(client =>
{
    client.BaseAddress = new Uri("https://nominatim.openstreetmap.org/");
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "AnteraApp/1.0 (+https://github.com/antera-dev/antera-app)");
});
builder.Services.AddHttpClient<AirQualityService>(client =>
{
    client.BaseAddress = new Uri("https://air-quality-api.open-meteo.com/");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient("OpenMeteoGeocoding", client =>
{
    client.BaseAddress = new Uri("https://geocoding-api.open-meteo.com/");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.Configure<PasswordHasherOptions>(options =>
{
    options.IterationCount = 210_000;
});
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

// JWT authentication setup
var key = Encoding.UTF8.GetBytes(jwtSettings.Secret);

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.RequireHttpsMetadata = false;
    options.SaveToken = true;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(key),
        ValidateIssuer = true,
        ValidIssuer = jwtSettings.Issuer,
        ValidateAudience = true,
        ValidAudience = jwtSettings.Audience,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromMinutes(1)
    };
});

builder.Services.AddAuthorization();

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend",
        policy => policy
            .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials()
    );
});

var app = builder.Build();

app.UseCors("AllowFrontend");
app.UseAuthentication();
app.UseAuthorization();

// Swagger
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/swagger") ||
        context.Request.Path.StartsWithSegments("/api-docs"))
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.CacheControl = "no-store, no-cache";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers.Expires = "0";
            return Task.CompletedTask;
        });
    }

    await next();
});

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.RoutePrefix = "api-docs";
    options.SwaggerEndpoint("/swagger/v1/swagger.json?v=net10", "AnteraApp.Api v1");
});

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.MapGet("/api/locations/search", async (
    string query,
    LocationService locationService,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(query) || query.Trim().Length is < 2 or > 80)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["query"] = ["Escribe entre 2 y 80 caracteres para buscar una localidad."]
        });
    }

    try
    {
        var locations = await locationService.SearchSpanishSettlementsAsync(query, cancellationToken);
        return Results.Ok(locations);
    }
    catch (Exception exception) when (exception is HttpRequestException or JsonException)
    {
        return Results.Problem(
            title: "No se han podido buscar localidades ahora mismo.",
            statusCode: StatusCodes.Status502BadGateway);
    }
})
.WithName("SearchSpanishLocations");

app.MapGet("/api/pollen/current", async (
    double latitude,
    double longitude,
    PollenService pollenService,
    CancellationToken cancellationToken) =>
{
    if (latitude is < -90 or > 90 || longitude is < -180 or > 180)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["coordinates"] = ["Las coordenadas indicadas no son válidas."]
        });
    }

    try
    {
        var reading = await pollenService.GetCurrentAsync(latitude, longitude, cancellationToken);
        return Results.Ok(reading);
    }
    catch (Exception exception) when (
        exception is HttpRequestException or JsonException or InvalidOperationException)
    {
        return Results.Problem(
            title: "No se han podido obtener los datos de polen.",
            statusCode: StatusCodes.Status502BadGateway);
    }
})
.WithName("GetCurrentPollen");

app.MapGet("/api/air-quality/current", async (
    double latitude,
    double longitude,
    AirQualityService airQualityService,
    CancellationToken cancellationToken) =>
{
    if (latitude is < -90 or > 90 || longitude is < -180 or > 180)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["coordinates"] = ["Las coordenadas indicadas no son válidas."]
        });
    }

    try
    {
        var reading = await airQualityService.GetCurrentAsync(latitude, longitude, cancellationToken);
        return Results.Ok(reading);
    }
    catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException)
    {
        return Results.Problem(
            title: "No se han podido obtener los datos de calidad del aire.",
            statusCode: StatusCodes.Status502BadGateway);
    }
})
.WithName("GetCurrentAirQuality");

app.MapGet("/api/pollen/types", () => Results.Ok(PollenCatalog.Types))
    .RequireAuthorization()
    .WithName("GetPollenTypes");

app.MapGet("/api/user/pollen-preferences", async (
    System.Security.Claims.ClaimsPrincipal user,
    PollenPreferencesService preferencesService,
    CancellationToken cancellationToken) =>
{
    var userId = user.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
        ?? user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

    var preferences = await preferencesService.GetAsync(userId, cancellationToken);
    return preferences is null ? Results.Unauthorized() : Results.Ok(preferences);
})
.RequireAuthorization()
.WithName("GetPollenPreferences");

app.MapPut("/api/user/pollen-preferences", async (
    System.Security.Claims.ClaimsPrincipal user,
    UpdatePollenPreferencesRequest request,
    PollenPreferencesService preferencesService,
    CancellationToken cancellationToken) =>
{
    var userId = user.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
        ?? user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

    var pollenTypeIds = request.PollenTypeIds?
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    if (pollenTypeIds is null || pollenTypeIds.Length > PollenCatalog.Types.Count ||
        pollenTypeIds.Any(id => !PollenCatalog.Contains(id)))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["pollenTypeIds"] = ["Los pólenes seleccionados no son válidos."]
        });
    }

    var preferences = await preferencesService.UpdateAsync(userId, pollenTypeIds, cancellationToken);
    return preferences is null ? Results.Unauthorized() : Results.Ok(preferences);
})
.RequireAuthorization()
.WithName("UpdatePollenPreferences");

app.MapGet("/api/user/profile", async (
    System.Security.Claims.ClaimsPrincipal user,
    AuthService authService,
    CancellationToken cancellationToken) =>
{
    var userId = GetUserId(user);
    if (userId is null) return Results.Unauthorized();

    var profile = await authService.GetProfileAsync(userId, cancellationToken);
    return profile is null ? Results.Unauthorized() : Results.Ok(profile);
})
.RequireAuthorization()
.WithName("GetUserProfile");

app.MapPut("/api/user/profile", async (
    System.Security.Claims.ClaimsPrincipal user,
    AuthService authService,
    UpdateUserProfileRequest request,
    CancellationToken cancellationToken) =>
{
    var userId = GetUserId(user);
    var name = request.Name?.Trim();
    if (userId is null) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(name) || name.Length > 80)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["name"] = ["El nombre debe tener entre 1 y 80 caracteres."]
        });
    }

    var profile = await authService.UpdateNameAsync(userId, name, cancellationToken);
    return profile is null ? Results.Unauthorized() : Results.Ok(profile);
})
.RequireAuthorization()
.WithName("UpdateUserProfile");

app.MapPut("/api/user/email", async (
    System.Security.Claims.ClaimsPrincipal user,
    AuthService authService,
    UpdateUserEmailRequest request,
    CancellationToken cancellationToken) =>
{
    var userId = GetUserId(user);
    var email = request.Email?.Trim().ToLowerInvariant();
    if (userId is null) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(email) || email.Length > 254 || !email.Contains('@') ||
        string.IsNullOrWhiteSpace(request.CurrentPassword))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["email"] = ["Introduce un correo electrónico válido y tu contraseña actual."],
            ["currentPassword"] = ["Introduce tu contraseña actual."]
        });
    }

    var profile = await authService.UpdateEmailAsync(userId, email, request.CurrentPassword, cancellationToken);
    return profile is null
        ? Results.BadRequest(new { message = "No se ha podido validar el cambio de correo electrónico." })
        : Results.Ok(profile);
})
.RequireAuthorization()
.WithName("UpdateUserEmail");

app.MapPut("/api/user/password", async (
    System.Security.Claims.ClaimsPrincipal user,
    AuthService authService,
    UpdateUserPasswordRequest request,
    CancellationToken cancellationToken) =>
{
    var userId = GetUserId(user);
    if (userId is null) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(request.CurrentPassword) ||
        string.IsNullOrWhiteSpace(request.NewPassword) ||
        request.NewPassword.Length is < 8 or > 128)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["password"] = ["La nueva contraseña debe tener entre 8 y 128 caracteres."]
        });
    }

    var updated = await authService.UpdatePasswordAsync(
        userId,
        request.CurrentPassword,
        request.NewPassword,
        cancellationToken);
    return updated switch
    {
        null => Results.Unauthorized(),
        false => Results.BadRequest(new { message = "La contraseña actual no es correcta." }),
        true => Results.NoContent()
    };
})
.RequireAuthorization()
.WithName("UpdateUserPassword");

app.MapDelete("/api/user/account", async (
    HttpResponse response,
    System.Security.Claims.ClaimsPrincipal user,
    AuthService authService,
    [Microsoft.AspNetCore.Mvc.FromBody] DeleteUserAccountRequest request,
    CancellationToken cancellationToken) =>
{
    var userId = GetUserId(user);
    if (userId is null) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(request.CurrentPassword))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["currentPassword"] = ["Introduce tu contraseña actual."]
        });
    }

    var deleted = await authService.DeleteAccountAsync(userId, request.CurrentPassword, cancellationToken);
    if (deleted is null) return Results.Unauthorized();
    if (deleted is false) return Results.BadRequest(new { message = "La contraseña actual no es correcta." });

    DeleteRefreshTokenCookie(response);
    return Results.NoContent();
})
.RequireAuthorization()
.WithName("DeleteUserAccount");

app.MapPost("/api/auth/register", async (
    AuthService authService,
    RegisterRequest request,
    CancellationToken cancellationToken) =>
{
    request.Name = request.Name?.Trim() ?? string.Empty;
    request.Email = request.Email?.Trim().ToLowerInvariant() ?? string.Empty;
    var validationErrors = new Dictionary<string, string[]>();
    if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 80)
        validationErrors["name"] = ["Introduce un nombre de usuario de hasta 80 caracteres."];
    if (string.IsNullOrWhiteSpace(request.Email) || request.Email.Length > 254 || !request.Email.Contains('@'))
        validationErrors["email"] = ["Introduce un correo electrónico válido."];
    if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length is < 8 or > 128)
        validationErrors["password"] = ["La contraseña debe tener entre 8 y 128 caracteres."];

    if (validationErrors.Count > 0)
    {
        return Results.ValidationProblem(validationErrors);
    }

    var token = await authService.RegisterAsync(request, cancellationToken);
    if (token is null)
        return Results.Conflict(new { field = "email", message = "Ya existe una cuenta con este correo electrónico." });

    return Results.Created("/api/user/pollen-preferences", new AuthResponse { Token = token });
})
.WithName("RegisterUser");

app.MapPost("/api/auth/login", async (
    HttpResponse response,
    AuthService authService,
    LoginRequest request,
    CancellationToken cancellationToken) =>
{
    var session = await authService.LoginAsync(request, cancellationToken);
    if (session == null)
        return Results.Unauthorized();

    if (session.RefreshToken is not null)
    {
        SetRefreshTokenCookie(
            response,
            session.RefreshToken,
            jwtSettings.RefreshTokenExpirationDays,
            !app.Environment.IsDevelopment());
    }
    else
    {
        DeleteRefreshTokenCookie(response);
    }

    return Results.Ok(new AuthResponse { Token = session.AccessToken });
})
.WithName("LoginUser");

app.MapPost("/api/auth/refresh", async (
    HttpRequest request,
    HttpResponse response,
    AuthService authService,
    CancellationToken cancellationToken) =>
{
    if (!request.Cookies.TryGetValue("antera_refresh", out var refreshToken) ||
        string.IsNullOrWhiteSpace(refreshToken))
    {
        return Results.Unauthorized();
    }

    var session = await authService.RefreshAsync(refreshToken, cancellationToken);
    if (session is null || session.RefreshToken is null)
    {
        DeleteRefreshTokenCookie(response);
        return Results.Unauthorized();
    }

    SetRefreshTokenCookie(
        response,
        session.RefreshToken,
        jwtSettings.RefreshTokenExpirationDays,
        !app.Environment.IsDevelopment());
    return Results.Ok(new AuthResponse { Token = session.AccessToken });
})
.WithName("RefreshUserSession");

app.MapPost("/api/auth/logout", async (
    HttpRequest request,
    HttpResponse response,
    AuthService authService,
    CancellationToken cancellationToken) =>
{
    if (request.Cookies.TryGetValue("antera_refresh", out var refreshToken) &&
        !string.IsNullOrWhiteSpace(refreshToken))
    {
        await authService.RevokeRefreshTokenAsync(refreshToken, cancellationToken);
    }

    DeleteRefreshTokenCookie(response);
    return Results.NoContent();
})
.WithName("LogoutUser");

// Ruta protegida por JWT
app.MapGet("/api/protected", [Authorize]() =>
{
    return Results.Ok("Access granted to protected route.");
})
.WithName("ProtectedRoute");

app.Run();

static void SetRefreshTokenCookie(
    HttpResponse response,
    string refreshToken,
    int expirationDays,
    bool secure)
{
    response.Cookies.Append("antera_refresh", refreshToken, new CookieOptions
    {
        HttpOnly = true,
        IsEssential = true,
        SameSite = SameSiteMode.Lax,
        Secure = secure,
        Expires = DateTimeOffset.UtcNow.AddDays(expirationDays),
        Path = "/api/auth"
    });
}

static void DeleteRefreshTokenCookie(HttpResponse response) =>
    response.Cookies.Delete("antera_refresh", new CookieOptions { Path = "/api/auth" });

static string? GetUserId(System.Security.Claims.ClaimsPrincipal user) =>
    user.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
    ?? user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
