using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using System.Text;
using System.Text.Json;
using MaxMind.GeoIP2;
using StackExchange.Redis;
using System.Net;

namespace UrlShortener.AnalyticsService;

public class AnalyticsService : ControllerBase
{
    private readonly AnalyticsDbContext _dbContext;
    private readonly IConnection _rabbitMqConnection;
    private readonly IConnectionMultiplexer _redis;
    private readonly DatabaseReader _geoReader;
    private readonly IConfiguration _configuration;

    public AnalyticsService(AnalyticsDbContext dbContext, IConnection rabbitMqConnection, 
        IConnectionMultiplexer redis, IConfiguration configuration)
    {
        _dbContext = dbContext;
        _rabbitMqConnection = rabbitMqConnection;
        _redis = redis;
        _configuration = configuration;
        _geoReader = new DatabaseReader(configuration["MaxMind:DatabasePath"]);
        StartRabbitMqConsumer();
    }

    // Consultar análises de uma URL
    [HttpGet("analytics/{shortCode}")]
    public async Task<IActionResult> GetAnalytics(string shortCode)
    {
        var userId = GetUserIdFromJwt();
        var url = await _dbContext.Urls.FirstOrDefaultAsync(u => u.ShortCode == shortCode && u.UserId == userId);
        if (url == null)
        {
            return NotFound("URL not found or not owned by user");
        }

        var clicks = await _dbContext.Clicks
            .Where(c => c.ShortCode == shortCode)
            .GroupBy(c => c.Timestamp.Date)
            .Select(g => new { Date = g.Key, Count = g.Count() })
            .ToListAsync();

        var totalClicks = clicks.Sum(c => c.Count);

        // Resumo por geolocalização
        var geoData = await _dbContext.Clicks
            .Where(c => c.ShortCode == shortCode)
            .GroupBy(c => new { c.Country, c.City })
            .Select(g => new { Country = g.Key.Country, City = g.Key.City, Count = g.Count() })
            .ToListAsync();

        return Ok(new
        {
            ShortCode = shortCode,
            TotalClicks = totalClicks,
            DailyClicks = clicks,
            GeoData = geoData
        });
    }

    private void StartRabbitMqConsumer()
    {
        var factory = new ConnectionFactory { HostName = "localhost" };
        var channel = _rabbitMqConnection.CreateModel();
        channel.QueueDeclare(queue: "clicks", durable: true, exclusive: false, autoDelete: false, arguments: null);

        var consumer = new EventingBasicConsumer(channel);
        consumer.Received += async (model, ea) =>
        {
            var body = ea.Body.ToArray();
            var message = Encoding.UTF8.GetString(body);
            var clickEvent = JsonSerializer.Deserialize<ClickEvent>(message);

            // Validar IP
            if (!IPAddress.TryParse(clickEvent.ClientIp, out var ipAddress))
            {
                channel.BasicNack(deliveryTag: ea.DeliveryTag, multiple: false, requeue: false);
                return;
            }

            // Consultar geolocalização
            var geoData = await GetGeoData(clickEvent.ClientIp);

            var click = new Click
            {
                ShortCode = clickEvent.ShortCode,
                ClientIp = clickEvent.ClientIp,
                Timestamp = DateTime.UtcNow,
                Country = geoData?.Country?.IsoCode,
                City = geoData?.City?.Name,
                Latitude = geoData?.Location?.Latitude,
                Longitude = geoData?.Location?.Longitude
            };

            _dbContext.Clicks.Add(click);
            await _dbContext.SaveChangesAsync();

            channel.BasicAck(deliveryTag: ea.DeliveryTag, multiple: false);
        };

        channel.BasicConsume(queue: "clicks", autoAck: false, consumer: consumer);
    }

    private async Task<CityResponse?> GetGeoData(string ip)
    {
        var redisDb = _redis.GetDatabase();
        var cachedGeo = await redisDb.StringGetAsync($"geo:{ip}");
        if (!cachedGeo.IsNullOrEmpty)
        {
            return JsonSerializer.Deserialize<CityResponse>(cachedGeo);
        }

        try
        {
            var response = _geoReader.City(IPAddress.Parse(ip));
            var geoData = new CityResponse
            {
                Country = response.Country,
                City = response.City,
                Location = response.Location
            };

            // Cachear por 24 horas
            await redisDb.StringSetAsync($"geo:{ip}", JsonSerializer.Serialize(geoData), TimeSpan.FromHours(24));
            return geoData;
        }
        catch (Exception)
        {
            return null; // Retornar null se a geolocalização falhar
        }
    }

    private string GetUserIdFromJwt()
    {
        return User.Claims.FirstOrDefault(c => c.Type == "sub")?.Value ?? throw new UnauthorizedAccessException();
    }
}

public class ClickEvent
{
    public string ShortCode { get; set; }
    public string ClientIp { get; set; }
}

public class Click
{
    public int Id { get; set; }
    public string ShortCode { get; set; }
    public string ClientIp { get; set; }
    public DateTime Timestamp { get; set; }
    public string? Country { get; set; }
    public string? City { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
}

// Modelo simplificado para serialização de dados do MaxMind
public class CityResponse
{
    public MaxMind.GeoIP2.Model.Country Country { get; set; }
    public MaxMind.GeoIP2.Model.City City { get; set; }
    public MaxMind.GeoIP2.Model.Location Location { get; set; }
}

public class AnalyticsDbContext : DbContext
{
    public AnalyticsDbContext(DbContextOptions<AnalyticsDbContext> options) : base(options) { }
    public DbSet<Click> Clicks { get; set; }
    public DbSet<UrlEntry> Urls { get; set; } // Para verificar propriedade da URL

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Click>()
            .HasIndex(c => new { c.ShortCode, c.Timestamp })
            .HasDatabaseName("IX_Clicks_ShortCode_Timestamp");
    }
}