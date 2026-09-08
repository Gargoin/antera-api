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
    .Validate(settings => settings.ExpirationMinutes is >= 5 and <= 1440,
        "Jwt:ExpirationMinutes must be between 5 and 1440.")
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
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "AnteraApp/1.0 (+https://github.com/antera-dev/antera-app)");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
});
builder.Services.AddHttpClient<CastillaLeonPollenService>(client =>
{
    client.BaseAddress = new Uri("https://analisis.datosabiertos.jcyl.es/");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient<CataloniaPollenService>(client =>
{
    client.BaseAddress = new Uri("https://aerobiologia.cat/");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient<LocationService>(client =>
{
    client.BaseAddress = new Uri("https://nominatim.openstreetmap.org/");
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "AnteraApp/1.0 (+https://github.com/antera-dev/antera-app)");
});
builder.Services.AddHttpClient("SpanishLocationAutocomplete", client =>
{
    client.BaseAddress = new Uri("https://www.cartociudad.es/geocoder/api/geocoder/");
    client.Timeout = TimeSpan.FromSeconds(10);
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
            .WithOrigins("http://localhost:5173")
            .AllowAnyMethod()
            .AllowAnyHeader()
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

app.UseHttpsRedirection();

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
.RequireAuthorization()
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
.RequireAuthorization()
.WithName("GetCurrentPollen");

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

app.MapPost("/api/auth/register", async (
    AuthService authService,
    RegisterRequest request,
    CancellationToken cancellationToken) =>
{
    var token = await authService.RegisterAsync(request, cancellationToken);
    if (token is null)
        return Results.BadRequest("User already exists.");

    return Results.Created("/api/user/pollen-preferences", new AuthResponse { Token = token });
})
.WithName("RegisterUser");

app.MapPost("/api/auth/login", async (
    AuthService authService,
    LoginRequest request,
    CancellationToken cancellationToken) =>
{
    var token = await authService.LoginAsync(request, cancellationToken);
    if (token == null)
        return Results.Unauthorized();

    return Results.Ok(new AuthResponse { Token = token });
})
.WithName("LoginUser");

// Ruta protegida por JWT
app.MapGet("/api/protected", [Authorize]() =>
{
    return Results.Ok("Access granted to protected route.");
})
.WithName("ProtectedRoute");

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
