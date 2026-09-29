FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["AnteraApp.Api.csproj", "./"]
RUN dotnet restore "AnteraApp.Api.csproj"

COPY . .
RUN dotnet publish "AnteraApp.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "AnteraApp.Api.dll"]
