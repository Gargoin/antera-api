namespace AnteraApp.Api.Models;

public sealed record AirQualityReadingDto(
    DateTime Date,
    string TimeZone,
    double EuropeanAqi,
    string Level,
    double Pm2_5,
    double Pm10,
    double NitrogenDioxide,
    double Ozone,
    string Source,
    string Notice);
