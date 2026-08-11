using AnteraApp.Api.Settings;
using AnteraApp.Api.Services;
using AnteraApp.Api.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using System.Text;
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

builder.Services.AddSingleton<AnteraService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<JwtService>();
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

// Rutas API
app.MapGet("/api/antera", [Authorize] async (AnteraService anteraService) =>
{
    var readings = await anteraService.GetAsync();
    return Results.Ok(readings);
})
.WithName("GetAnteraReadings");

app.MapPost("/api/antera", async (AnteraService anteraService, AnteraReading newReading) =>
{
    await anteraService.CreateAsync(newReading);
    return Results.Created($"/api/antera/{newReading.Id}", newReading);
})
.RequireAuthorization()
.WithName("CreateAnteraReading");

app.MapPost("/api/auth/register", async (
    AuthService authService,
    RegisterRequest request,
    CancellationToken cancellationToken) =>
{
    var success = await authService.RegisterAsync(request, cancellationToken);
    if (!success)
        return Results.BadRequest("User already exists.");

    return Results.Ok("User registered successfully.");
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
