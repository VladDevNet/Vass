using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using VoiceAssistant.API.Data;
using VoiceAssistant.API.Data.Entities;
using VoiceAssistant.API.Services;

var builder = WebApplication.CreateBuilder(args);

// Database
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

// Identity
builder.Services.AddIdentity<User, IdentityRole>(options =>
{
    options.Password.RequireDigit = false;
    options.Password.RequireUppercase = false;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequiredLength = 6;
    options.User.RequireUniqueEmail = true;
})
.AddEntityFrameworkStores<AppDbContext>()
.AddDefaultTokenProviders();

// JWT Authentication
var jwtSecret = builder.Configuration["Jwt:Secret"]!;
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidAudience = builder.Configuration["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret))
    };
});

builder.Services.AddAuthorization();

builder.Services.AddHttpClient();

// Services
builder.Services.AddSingleton<GeminiService>();
builder.Services.AddSingleton<PiperTtsService>();
builder.Services.AddSingleton<CompanionPromptService>();
builder.Services.AddSingleton<AudioAnalysisService>();
builder.Services.AddSingleton<SpeakerIdService>();
builder.Services.AddSingleton<SpeakerPendingStore>();
builder.Services.AddScoped<SpeakerRegistryService>();
builder.Services.AddScoped<ConversationMemoryService>();

// Controllers
builder.Services.AddControllers();

// CORS (dev)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

// The api container publishes no ports (see docker-compose.yml) — nginx is
// the ONLY thing that can reach it, so X-Forwarded-For on the immediate
// connection is always nginx's, never spoofable by a caller bypassing the
// proxy. Without this, Connection.RemoteIpAddress is nginx's fixed internal
// container IP for every request, collapsing the rate limiter below into one
// shared bucket for the whole app instead of one per real attacker.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    // Two real hops in production (confirmed against the actual VPS config):
    // the external Caddy proxy sets X-Forwarded-For to the real client, then
    // nginx (Caddy -> localhost:4001 -> this container) appends its own
    // loopback-perceived address on top. ForwardLimit's default of 1 only
    // unwinds the nearest hop (nginx's own), landing on 127.0.0.1 instead of
    // the client. Must unwind both to reach the real value.
    options.ForwardLimit = 2;
});

// Rate limiting for anonymous auth endpoints (PROJECT-AUDIT-2026-07-10 SEC-01).
// Partitioned per client IP, since these endpoints have no user identity yet.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("auth", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));

    // device-link/redeem guards a 6-digit code (1,000,000 possibilities) valid
    // for 10 minutes — the limit must make brute-forcing it within that window
    // infeasible, not just "rate limited eventually."
    options.AddPolicy("device-link-redeem", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(10),
            QueueLimit = 0
        }));
});

var app = builder.Build();

// Auto-migrate on startup
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
    await MigrateLegacyApiKeysAsync(db, scope.ServiceProvider.GetRequiredService<IConfiguration>());
}

// One-time, idempotent: re-saves any UserSettings row whose key fields
// predate ApiKeyEncryptionConverter (PROJECT-AUDIT-2026-07-10 SEC-03) through
// the same converter, encrypting them. Detects "still legacy plaintext" via
// a raw ADO.NET read (bypassing EF Core's own lenient, always-succeeds
// Decrypt) and DecryptStrict, which throws for anything that isn't real
// ciphertext under this key -- that's what "not yet migrated" means here.
// Safe to run on every startup: already-encrypted rows decrypt cleanly under
// DecryptStrict and are skipped.
async Task MigrateLegacyApiKeysAsync(AppDbContext db, IConfiguration configuration)
{
    var key = ApiKeyEncryption.DeriveKey(configuration["Encryption:Key"]!);

    var idsNeedingMigration = new List<int>();
    await db.Database.OpenConnectionAsync();
    try
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT \"Id\", \"OpenAiApiKey\", \"AnthropicApiKey\", \"GeminiApiKey\" FROM \"UserSettings\"";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetInt32(0);
            var openAi = reader.IsDBNull(1) ? null : reader.GetString(1);
            var anthropic = reader.IsDBNull(2) ? null : reader.GetString(2);
            var gemini = reader.IsDBNull(3) ? null : reader.GetString(3);
            if (NeedsMigration(openAi, key) || NeedsMigration(anthropic, key) || NeedsMigration(gemini, key))
            {
                idsNeedingMigration.Add(id);
            }
        }
    }
    finally
    {
        await db.Database.CloseConnectionAsync();
    }

    if (idsNeedingMigration.Count == 0) return;

    var toMigrate = await db.UserSettings.Where(s => idsNeedingMigration.Contains(s.Id)).ToListAsync();
    foreach (var settings in toMigrate)
    {
        // Already holds the correct plaintext -- EF Core's lenient Decrypt
        // returned it as-is on load since it wasn't real ciphertext yet.
        // Explicitly marking modified forces a write despite the *logical*
        // value looking unchanged, so the encrypting converter runs on save.
        var entry = db.Entry(settings);
        entry.Property(s => s.OpenAiApiKey).IsModified = true;
        entry.Property(s => s.AnthropicApiKey).IsModified = true;
        entry.Property(s => s.GeminiApiKey).IsModified = true;
    }
    await db.SaveChangesAsync();
    app.Logger.LogInformation("Encrypted {Count} UserSettings row(s) with legacy plaintext API keys", toMigrate.Count);
}

static bool NeedsMigration(string? rawValue, byte[] key)
{
    if (string.IsNullOrEmpty(rawValue)) return false;
    try
    {
        ApiKeyEncryption.DecryptStrict(rawValue, key);
        return false;
    }
    catch (FormatException) { return true; }
    catch (CryptographicException) { return true; }
}

// Must run before anything that reads Connection.RemoteIpAddress (rate
// limiting below, and anything else added later) — it rewrites that value
// from the forwarded header, so ordering here is not cosmetic.
app.UseForwardedHeaders();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();
app.MapGet("/api/health", () => Results.Ok("healthy"));

app.Run();
