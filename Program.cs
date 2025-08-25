using AnteraApp.Api.Settings;
using AnteraApp.Api.Services;
using AnteraApp.Api.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
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
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            new string[] {}
        }
    });
});

builder.Services.Configure<MongoDBSettings>(
    builder.Configuration.GetSection("MongoDB"));

builder.Services.Configure<JwtSettings>(
    builder.Configuration.GetSection("Jwt"));

builder.Services.AddSingleton<AnteraService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<JwtService>();

// JWT authentication setup
var jwtKey = builder.Configuration["Jwt:Secret"];
var key = Encoding.ASCII.GetBytes(jwtKey!);

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
        ValidateIssuer = false,
        ValidateAudience = false
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
app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();

// Rutas API
app.MapGet("/api/antera", [Authorize] async (AnteraService anteraService) =>
{
    var readings = await anteraService.GetAsync();
    return Results.Ok(readings);
})
.WithName("GetAnteraReadings")
.WithOpenApi();

app.MapPost("/api/antera", async (AnteraService anteraService, AnteraReading newReading) =>
{
    await anteraService.CreateAsync(newReading);
    return Results.Created($"/api/antera/{newReading.Id}", newReading);
})
.WithName("CreateAnteraReading")
.WithOpenApi();

app.MapPost("/api/auth/register", async (AuthService authService, RegisterRequest request) =>
{
    var success = await authService.RegisterAsync(request);
    if (!success)
        return Results.BadRequest("User already exists.");

    return Results.Ok("User registered successfully.");
})
.WithName("RegisterUser")
.WithOpenApi();

app.MapPost("/api/auth/login", async (AuthService authService, LoginRequest request) =>
{
    var token = await authService.LoginAsync(request);
    if (token == null)
        return Results.Unauthorized();

    return Results.Ok(new AuthResponse { Token = token });
})
.WithName("LoginUser")
.WithOpenApi();

// Ruta protegida por JWT
app.MapGet("/api/protected", [Authorize]() =>
{
    return Results.Ok("Access granted to protected route.");
})
.WithName("ProtectedRoute")
.WithOpenApi();

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
