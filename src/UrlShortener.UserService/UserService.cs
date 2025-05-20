using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace UrlShortener.UserService;

public class UserService : ControllerBase
{
    private readonly UserDbContext _dbContext;
    private readonly IConnectionMultiplexer _redis;
    private readonly IConfiguration _configuration;

    public UserService(UserDbContext dbContext, IConnectionMultiplexer redis, IConfiguration configuration)
    {
        _dbContext = dbContext;
        _redis = redis;
        _configuration = configuration;
    }

    // Registro de usuário
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        if (await _dbContext.Users.AnyAsync(u => u.Email == request.Email))
        {
            return BadRequest("Email already exists");
        }

        var user = new User
        {
            Email = request.Email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            Plan = Plan.Free,
            UrlQuota = 10, // Limite inicial para plano gratuito
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();

        // Armazenar cota no Redis
        var redisDb = _redis.GetDatabase();
        await redisDb.StringSetAsync($"quota:{user.Id}", user.UrlQuota, TimeSpan.FromDays(30));

        return Ok(new { Message = "User registered successfully" });
    }

    // Login de usuário
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Email == request.Email);
        if (user == null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            return Unauthorized("Invalid credentials");
        }

        var token = GenerateJwtToken(user);
        return Ok(new { Token = token });
    }

    // Verificar cota do usuário
    [HttpGet("users/{userId}/quota")]
    public async Task<IActionResult> GetQuota(string userId)
    {
        // Verificar cache
        var redisDb = _redis.GetDatabase();
        var cachedQuota = await redisDb.StringGetAsync($"quota:{userId}");
        if (!cachedQuota.IsNullOrEmpty)
        {
            return Ok(new { Quota = int.Parse(cachedQuota), Used = await GetUsedUrls(userId) });
        }

        // Buscar no banco
        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
        {
            return NotFound("User not found");
        }

        // Atualizar cache
        await redisDb.StringSetAsync($"quota:{userId}", user.UrlQuota, TimeSpan.FromDays(30));

        return Ok(new { Quota = user.UrlQuota, Used = await GetUsedUrls(userId) });
    }

    // Simular compra de plano
    [HttpPost("upgrade-plan")]
    public async Task<IActionResult> UpgradePlan([FromBody] UpgradePlanRequest request)
    {
        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == GetUserIdFromJwt());
        if (user == null)
        {
            return NotFound("User not found");
        }

        // Simular lógica de pagamento (ex.: integração com Stripe seria aqui)
        user.Plan = request.Plan;
        user.UrlQuota = request.Plan switch
        {
            Plan.Basic => 100,
            Plan.Premium => 1000,
            _ => 10 // Plano gratuito
        };

        _dbContext.Users.Update(user);
        await _dbContext.SaveChangesAsync();

        // Atualizar cache
        var redisDb = _redis.GetDatabase();
        await redisDb.StringSetAsync($"quota:{user.Id}", user.UrlQuota, TimeSpan.FromDays(30));

        return Ok(new { Message = "Plan upgraded successfully", NewQuota = user.UrlQuota });
    }

    private async Task<int> GetUsedUrls(string userId)
    {
        // Consultar URL Service para contar URLs do usuário
        // Simulação: supomos que o URL Service retorna a contagem
        return await _dbContext.Urls.CountAsync(u => u.UserId == userId);
    }

    private string GenerateJwtToken(User user)
    {
        var key = Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]);
        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim("sub", user.Id),
                new Claim("email", user.Email),
                new Claim("plan", user.Plan.ToString())
            }),
            Expires = DateTime.UtcNow.AddDays(7),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
        };
        var tokenHandler = new JwtSecurityTokenHandler();
        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }

    private string GetUserIdFromJwt()
    {
        return User.Claims.FirstOrDefault(c => c.Type == "sub")?.Value ?? throw new UnauthorizedAccessException();
    }
}

public class RegisterRequest
{
    public string Email { get; set; }
    public string Password { get; set; }
}

public class LoginRequest
{
    public string Email { get; set; }
    public string Password { get; set; }
}

public class UpgradePlanRequest
{
    public Plan Plan { get; set; }
}

public enum Plan
{
    Free,
    Basic,
    Premium
}

public class User
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Email { get; set; }
    public string PasswordHash { get; set; }
    public Plan Plan { get; set; }
    public int UrlQuota { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class UserDbContext : DbContext
{
    public UserDbContext(DbContextOptions<UserDbContext> options) : base(options) { }
    public DbSet<User> Users { get; set; }
    public DbSet<UrlEntry> Urls { get; set; } // Para consulta de URLs usadas
}