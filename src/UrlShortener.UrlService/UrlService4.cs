using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace UrlShortener.UrlService;

public class UrlService : ControllerBase
{
    private readonly UrlDbContext _dbContext;
    private readonly IConnectionMultiplexer _redis;
    private readonly HttpClient _httpClient;

    public UrlService(UrlDbContext dbContext, IConnectionMultiplexer redis, HttpClient httpClient)
    {
        _dbContext = dbContext;
        _redis = redis;
        _httpClient = httpClient;
    }

    [HttpPost("shorten")]
    public async Task<IActionResult> ShortenUrl([FromBody] ShortenUrlRequest request)
    {
        if (!IsValidUrl(request.Url))
        {
            return BadRequest("Invalid URL");
        }

        var userId = GetUserIdFromJwt();
        if (!await HasQuotaAsync(userId))
        {
            return BadRequest("URL quota exceeded");
        }

        var shortCode = await GenerateShortCodeAsync();
        var urlEntry = new UrlEntry
        {
            ShortCode = shortCode,
            OriginalUrl = request.Url,
            UserId = userId,
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.Urls.Add(urlEntry);
        await _dbContext.SaveChangesAsync();

        // Cachear a URL no Redis por 30 dias
        var redisDb = _redis.GetDatabase();
        await redisDb.StringSetAsync($"url:{shortCode}", request.Url, TimeSpan.FromDays(30));

        return Ok(new { ShortUrl = $"http://{Request.Host}/{shortCode}" });
    }

    [HttpGet("{shortCode}")]
    public async Task<IActionResult> RedirectUrl(string shortCode)
    {
        // Verificar cache primeiro
        var redisDb = _redis.GetDatabase();
        var cachedUrl = await redisDb.StringGetAsync($"url:{shortCode}");
        if (!cachedUrl.IsNullOrEmpty)
        {
            // Enviar evento de clique para o RabbitMQ
            await SendClickEventAsync(shortCode, HttpContext.Connection.RemoteIpAddress?.ToString());
            return Redirect(cachedUrl);
        }

        // Buscar no banco
        var urlEntry = await _dbContext.Urls.FirstOrDefaultAsync(u => u.ShortCode == shortCode);
        if (urlEntry == null)
        {
            return NotFound("URL not found");
        }

        // Cachear por 30 dias
        await redisDb.StringSetAsync($"url:{shortCode}", urlEntry.OriginalUrl, TimeSpan.FromDays(30));

        // Enviar evento de clique para o RabbitMQ
        await SendClickEventAsync(shortCode, HttpContext.Connection.RemoteIpAddress?.ToString());
        return Redirect(urlEntry.OriginalUrl);
    }

    private async Task<string> GenerateShortCodeAsync()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        const int maxAttempts = 5;
        var redisDb = _redis.GetDatabase();
        var random = new Random();

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            var shortCode = new string(Enumerable.Repeat(chars, 6)
                .Select(s => s[random.Next(s.Length)]).ToArray());

            // Verificar no Redis
            if (!await redisDb.SetContainsAsync("short_codes", shortCode))
            {
                // Verificar no banco
                if (!await _dbContext.Urls.AnyAsync(u => u.ShortCode == shortCode))
                {
                    // Adicionar ao conjunto Redis para verificações futuras
                    await redisDb.SetAddAsync("short_codes", shortCode);
                    return shortCode;
                }
            }
        }

        throw new InvalidOperationException("Unable to generate a unique short code after maximum attempts");
    }

    private bool IsValidUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uriResult) &&
               (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);
    }

    private async Task<bool> HasQuotaAsync(string userId)
    {
        var response = await _httpClient.GetAsync($"http://user-service:8080/users/{userId}/quota");
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var quotaData = JsonSerializer.Deserialize<QuotaResponse>(await response.Content.ReadAsStringAsync());
        return quotaData.Quota > quotaData.Used;
    }

    private async Task SendClickEventAsync(string shortCode, string clientIp)
    {
        // Simulação de envio para RabbitMQ (implementação real usaria RabbitMQ.Client)
        // Aqui apenas para manter o exemplo funcional
    }

    private string GetUserIdFromJwt()
    {
        return User.Claims.FirstOrDefault(c => c.Type == "sub")?.Value ?? throw new UnauthorizedAccessException();
    }
}

public class ShortenUrlRequest
{
    public string Url { get; set; }
}

public class QuotaResponse
{
    public int Quota { get; set; }
    public int Used { get; set; }
}

public class UrlEntry
{
    public string ShortCode { get; set; }
    public string OriginalUrl { get; set; }
    public string UserId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class UrlDbContext : DbContext
{
    public UrlDbContext(DbContextOptions<UrlDbContext> options) : base(options) { }
    public DbSet<UrlEntry> Urls { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UrlEntry>()
            .HasIndex(u => u.ShortCode)
            .IsUnique();
    }
}