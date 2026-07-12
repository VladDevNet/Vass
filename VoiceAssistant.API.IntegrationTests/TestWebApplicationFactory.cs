using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VoiceAssistant.API.Data;

namespace VoiceAssistant.API.IntegrationTests;

// One instance per test class (xUnit IClassFixture) -- an isolated SQLite
// file DB, torn down in DisposeAsync. Program.cs's EF Core migrations
// contain Postgres-specific SQL Sqlite can't run, so Program.cs skips them
// entirely under the "Testing" environment (PROJECT-AUDIT-2026-07-10 QA-01)
// and this factory calls EnsureCreatedAsync from the current model instead.
public class TestWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"vass-it-{Guid.NewGuid():N}.db");

    // Program.cs's original options.UseNpgsql(...) call registers Npgsql's own
    // internal EF Core services (IDatabaseProvider etc.) directly into the
    // app's shared IServiceCollection as a side effect -- separate descriptors
    // from DbContextOptions<AppDbContext> itself, so RemoveAll<...>() below
    // never touches them. Left in place, AppDbContext's own internal service
    // resolution sees BOTH Npgsql's and Sqlite's provider services registered
    // at once and throws ("Only a single database provider can be registered
    // in a service provider"). A dedicated internal service provider --
    // built once, containing only Sqlite's EF services -- isolates Sqlite's
    // resolution from that shared container entirely, exactly as the
    // resulting exception's own message recommends ("maintaining one service
    // provider per database provider"). Static/shared across every
    // TestWebApplicationFactory instance: it holds no connection-specific
    // state, only EF Core's internal service graph, safe and intended to be
    // reused (same rationale as a real app building it once for its lifetime).
    private static readonly IServiceProvider SqliteInternalServices =
        new ServiceCollection().AddEntityFrameworkSqlite().BuildServiceProvider();

    // Real process environment variables, not ConfigureAppConfiguration --
    // Program.cs reads Jwt:Secret/Issuer/Audience directly off
    // builder.Configuration into local variables for AddJwtBearer's
    // IssuerSigningKey/ValidIssuer/ValidAudience BEFORE builder.Build() runs,
    // i.e. before WebApplicationFactory's own ConfigureWebHost overrides are
    // guaranteed to be layered in. AuthController.GenerateToken signs tokens
    // using the SAME keys read later via injected IConfiguration at request
    // time, which WOULD see a ConfigureAppConfiguration override -- a
    // mismatch between the two reads would 401 every authenticated test.
    // Env vars sidestep the question entirely: WebApplication.CreateBuilder
    // adds AddEnvironmentVariables() as one of its first sources, before any
    // of Program.cs's own code runs, so both reads are guaranteed to agree.
    static TestWebApplicationFactory()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
        Environment.SetEnvironmentVariable("Jwt__Secret", "integration-test-jwt-secret-min-32-chars-long!!");
        Environment.SetEnvironmentVariable("Jwt__Issuer", "Vass");
        Environment.SetEnvironmentVariable("Jwt__Audience", "Vass");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // >=16 bytes, required by ApiKeyEncryption.DeriveKey (AppDbContext's
                // constructor, read via injected IConfiguration -- safe here).
                ["Encryption:Key"] = "integration-test-encryption-key-not-a-real-secret",
                // Non-empty so GeminiService's fallback-to-default-key path doesn't
                // throw GeminiApiException before ever reaching FakeGeminiHandler --
                // never sent anywhere real, the HTTP client itself is faked below.
                ["Gemini:ApiKey"] = "test-fake-gemini-key",
            });
        });

        builder.ConfigureServices(services =>
        {
            // Remove the Npgsql-backed factory + derived scoped AppDbContext
            // Program.cs registered (REL-01's factory-plus-AddScoped shape) and
            // replace with Sqlite-backed equivalents in the same shape, so tests
            // exercise the real DI wiring against a different provider.
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<IDbContextFactory<AppDbContext>>();
            services.RemoveAll<AppDbContext>();
            // EF Core 8+ accumulates one entry per AddDbContext/AddDbContextFactory
            // call instead of the later call replacing the earlier one -- without
            // removing Program.cs's original entry too, DbContextOptions<AppDbContext>
            // gets built by applying BOTH options.UseNpgsql(...) AND the UseSqlite(...)
            // below to the same builder ("Multiple relational database provider
            // configurations found", confirmed empirically).
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();

            services.AddDbContextFactory<AppDbContext>(options => options
                .UseSqlite($"Data Source={_dbPath}")
                .UseInternalServiceProvider(SqliteInternalServices));
            services.AddScoped<AppDbContext>(sp =>
                sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

            // GeminiService/AudioAnalysisService/PiperTtsService/SpeakerIdService all
            // resolve their HttpClient via IHttpClientFactory.CreateClient() (the
            // unnamed, empty-string-named default client) -- overriding that name's
            // primary handler routes every one of them through FakeGeminiHandler.
            // Only GeminiService is actually exercised by these tests today.
            services.AddHttpClient(string.Empty)
                .ConfigurePrimaryHttpMessageHandler(() => new FakeGeminiHandler());
        });
    }

    public async Task InitializeAsync()
    {
        using var scope = Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        await base.DisposeAsync();
    }
}
