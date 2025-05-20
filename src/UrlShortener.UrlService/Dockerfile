# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["UrlShortener.UrlService/UrlShortener.UrlService.csproj", "UrlShortener.UrlService/"]
RUN dotnet restore "UrlShortener.UrlService/UrlShortener.UrlService.csproj"
COPY . .
WORKDIR "/src/UrlShortener.UrlService"
RUN dotnet build "UrlShortener.UrlService.csproj" -c Release -o /app/build

# Stage 2: Publish
FROM build AS publish
RUN dotnet publish "UrlShortener.UrlService.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Stage 3: Final
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s \
  CMD curl --fail http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "UrlShortener.UrlService.dll"]