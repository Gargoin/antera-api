using AnteraApp.Api.Settings;
using AnteraApp.Api.Services;
using AnteraApp.Api.Models;


var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.Configure<MongoDBSettings>(
builder.Configuration.GetSection("MongoDB"));

builder.Services.AddSingleton<AnteraService>();
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


// Configure the HTTP request pipeline.
app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();

app.MapGet("/api/antera", async (AnteraService anteraService) =>
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


var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast =  Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast")
.WithOpenApi();

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
