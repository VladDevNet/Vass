using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using VoiceAssistant.API.Controllers;
using VoiceAssistant.API.Data;
using VoiceAssistant.API.Data.Entities;
using VoiceAssistant.API.Services;

var builder = WebApplication.CreateBuilder(args);

// Database
//
// Registered as a factory only, with the usual scoped AppDbContext derived
// from it below -- NOT `AddDbContext` + `AddDbContextFactory` side by side.
// Both register DbContextOptions<AppDbContext> via TryAdd, so whichever call
// comes first silently wins that registration's lifetime; the loser ends up
// a Singleton (the factory) captively depending on a Scoped service, which
// throws "Cannot consume scoped service 'DbContextOptions<AppDbContext>'
// from singleton 'IDbContextFactory<AppDbContext>'" the moment
// ASP.NET Core's Development-environment DI validation runs at
// `builder.Build()` -- i.e. every local `dotnet run`. Confirmed empirically
// during REL-01 review by actually running the built binary. This pattern
// (factory + a derived AddScoped) is EF Core's own documented way to get
// both a normal scoped DbContext AND an independent-instance factory from
// one configuration.
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddScoped<AppDbContext>(sp =>
    sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

// A scoped DbContext must never have more than one operation in flight at a
// time. ChatController.MaybeUpdateCustomInstructionsAsync deliberately runs
// concurrently with the rest of Send() (started but not immediately awaited,
// so the response isn't held up by its own Gemini side-call) -- it needs an
// independent context instance, not the request's shared scoped one (above),
// or its SaveChangesAsync can race the main flow's own and throw "A second
// operation was started on this context before a previous operation
// completed" (PROJECT-AUDIT-2026-07-10 REL-01). It gets one via the same
// IDbContextFactory<AppDbContext> registered above.

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
        return false; // already valid ciphertext under the current key
    }
    catch (FormatException)
    {
        return true; // not base64 at all -- definitely legacy plaintext
    }
    catch (CryptographicException)
    {
        // Valid-length base64 whose GCM tag doesn't verify under this key.
        // Ambiguous: could be legacy plaintext that coincidentally decodes,
        // OR real ciphertext from a DIFFERENT key (e.g. Encryption:Key
        // changed since the last deploy). Silently treating this as "needs
        // migration" would re-encrypt it under the new key, permanently
        // destroying the original with no way to recover it (review
        // finding). Refuse to guess.
        throw new InvalidOperationException(
            "A UserSettings API key value is valid-length base64 but does not decrypt under the configured " +
            "Encryption:Key. Refusing to auto-migrate: this is ambiguous between legacy plaintext and ciphertext " +
            "encrypted under a DIFFERENT key, and guessing wrong would permanently destroy the value. If " +
            "Encryption:Key changed, restore the original key before starting this version.");
    }
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

// Liveness -- unchanged, deliberately just "is the process up and
// responding." This is what Docker's own healthcheck (docker-compose.yml)
// and the external Caddy/monitoring probe -- keep it cheap and dependency-free
// so a slow/degraded dependency never causes Docker to restart a
// perfectly-alive container.
app.MapGet("/api/health", () => Results.Ok("healthy"));

// Readiness -- can this instance actually serve the user-facing flow right
// now? Checks real DB connectivity and that the audio volume is writable
// (both hard requirements -- reflected in the overall status/HTTP code).
// TTS is checked but reported as its own degraded-dependency field, not a
// hard failure, per PROJECT-AUDIT-2026-07-10 REL-03's recommendation: a
// synthesis outage shouldn't make monitoring treat the whole API as down.
// Gemini is checked by CONFIGURATION presence only (never a real paid API
// call) -- also per REL-03, so probing readiness doesn't cost money/quota.
app.MapGet("/api/health/ready", async (
    AppDbContext db,
    PiperTtsService tts,
    IConfiguration configuration,
    IWebHostEnvironment hostEnv,
    ILogger<Program> readinessLogger) =>
{
    var checks = new Dictionary<string, string>();
    var ready = true;

    try
    {
        // Explicit bound, matching the deliberate 3s cap on the TTS check
        // below -- Npgsql's own default connect timeout (15s) would
        // otherwise apply, which is a real bound but an inconsistent one.
        using var dbTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        checks["database"] = await db.Database.CanConnectAsync(dbTimeout.Token) ? "ok" : "unavailable";
        if (checks["database"] != "ok") ready = false;
    }
    catch (Exception ex)
    {
        checks["database"] = "unavailable";
        ready = false;
        readinessLogger.LogWarning(ex, "Readiness check: database unreachable");
    }

    try
    {
        var audioPath = ChatController.ResolveAudioPath(configuration["Audio:Path"], hostEnv.ContentRootPath);
        Directory.CreateDirectory(audioPath);
        var probeFile = Path.Combine(audioPath, $".readiness-probe-{Guid.NewGuid():N}");
        // A handful of real bytes, not zero -- a 0-byte file needs only a
        // directory entry on most filesystems and can succeed even on a
        // volume with no free data blocks left, missing exactly the
        // "volume is actually writable" failure mode this check exists for.
        await File.WriteAllBytesAsync(probeFile, "readiness"u8.ToArray());
        checks["storage"] = "ok";
        try
        {
            File.Delete(probeFile);
        }
        catch (Exception deleteEx)
        {
            // The write already proved storage is writable -- that's what
            // this check reports on. A failed cleanup shouldn't flip a
            // working volume to "unavailable"; just log so leaked probe
            // files are traceable rather than silently accumulating.
            readinessLogger.LogWarning(deleteEx, "Readiness check: failed to delete probe file {ProbeFile}", probeFile);
        }
    }
    catch (Exception ex)
    {
        checks["storage"] = "unavailable";
        ready = false;
        readinessLogger.LogWarning(ex, "Readiness check: audio storage not writable");
    }

    checks["gemini"] = string.IsNullOrWhiteSpace(configuration["Gemini:ApiKey"]) ? "not_configured" : "configured";
    if (checks["gemini"] == "not_configured") ready = false;

    checks["tts"] = await tts.IsHealthyAsync() ? "ok" : "degraded";

    var payload = new { status = ready ? "ready" : "not_ready", checks };
    return ready ? Results.Ok(payload) : Results.Json(payload, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.Run();
