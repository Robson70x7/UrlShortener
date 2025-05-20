# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["UrlShortener.AnalyticsService/UrlShortener.AnalyticsService.csproj", "UrlShortener.AnalyticsService/"]
RUN dotnet restore "UrlShortener.AnalyticsService/UrlShortener.AnalyticsService.csproj"
COPY . .
WORKDIR "/src/UrlShortener.AnalyticsService"
RUN dotnet build "UrlShortener.AnalyticsService.csproj" -c Release -o /app/build

# Stage 2: Publish
FROM build AS publish
RUN dotnet publish "UrlShortener.AnalyticsService.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Stage 3: Final
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .
COPY data/maxmind/GeoLite2-City.mmdb /app/maxmind/GeoLite2-City.mmdb
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV MaxMind__DatabasePath=/app/maxmind/GeoLite2-City.mmdb
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s \
  CMD curl --fail http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "UrlShortener.AnalyticsService.dll"]