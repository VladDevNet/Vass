using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VoiceAssistant.API.Data;
using VoiceAssistant.API.Data.Entities;
using Xunit;

namespace VoiceAssistant.API.Tests;

// Regression coverage for PROJECT-AUDIT-2026-07-10 REL-01: ChatController.Send()
// starts MaybeUpdateCustomInstructionsAsync without awaiting it -- it genuinely
// runs concurrently with the rest of Send() (including a later SaveChangesAsync
// for the assistant message) until awaited near the end. A scoped EF Core
// DbContext supports exactly one operation in flight at a time.
//
// Both tests issue two operations with NO intervening await in the SOURCE code
// (fired in the same synchronous burst). Against a near-empty Sqlite database
// this alone isn't enough -- the first operation can complete synchronously-fast
// before the second is even issued, so there's no real overlap. A short
// DbCommandInterceptor delay forces the first operation to genuinely still be
// in flight, without relying on a wide, timing-sensitive race window (which is
// exactly the kind of "floating"/intermittent failure the audit describes in
// production, and is a poor fit for a reliable regression test).
public class ChatControllerConcurrencyTests
{
    private static IConfiguration BuildConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Encryption:Key"] = "test-harness-not-a-real-secret-0123456789",
            })
            .Build();

    private class DelayInterceptor(TimeSpan delay) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task SharedDbContext_TwoOperationsWithNoAwaitGap_SecondThrows()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"rel01-shared-{Guid.NewGuid():N}.db");
        try
        {
            var plainOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={dbPath};Foreign Keys=False")
                .Options;
            await using (var seedDb = new AppDbContext(plainOptions, BuildConfig()))
            {
                await seedDb.Database.EnsureCreatedAsync();
            }

            // Pre-compiles both query shapes used below (expression tree +
            // shaper generation is one-time, synchronous, CPU-bound work --
            // under real parallel load competing for scarce cores it can take
            // well over 100ms on its own, eating into the interceptor's delay
            // margin and letting the "second" operation below start only
            // after the first has already released its critical section).
            // Confirmed via REL-01 review: without this, the test failed
            // 13/13 times under 5-8-way parallel `dotnet test` contention.
            await using (var warmDb = new AppDbContext(plainOptions, BuildConfig()))
            {
                warmDb.ChatSessions.Add(new ChatSession { UserId = "warmup", Mode = "dialog", Title = "t" });
                await warmDb.SaveChangesAsync();
                await warmDb.UserSettings.ToListAsync();
            }

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={dbPath};Foreign Keys=False")
                .AddInterceptors(new DelayInterceptor(TimeSpan.FromMilliseconds(200)))
                .Options;

            // Mirrors the pre-fix shape: MaybeUpdateCustomInstructionsAsync and
            // Send() both used the SAME "_db" instance. Issuing a save and a
            // query back-to-back with no await between them, while the first
            // is still genuinely in flight (interceptor delay), reproduces the
            // exact contract violation EF Core refuses.
            await using var sharedDb = new AppDbContext(options, BuildConfig());
            sharedDb.ChatSessions.Add(new ChatSession { UserId = "user-1", Mode = "dialog", Title = "t" });
            var saveTask = sharedDb.SaveChangesAsync();
            var queryTask = sharedDb.UserSettings.ToListAsync();

            Exception? saveEx = null, queryEx = null;
            try { await saveTask; } catch (Exception ex) { saveEx = ex; }
            try { await queryTask; } catch (Exception ex) { queryEx = ex; }

            var thrown = new Exception?[] { saveEx, queryEx }
                .OfType<InvalidOperationException>()
                .FirstOrDefault(ex => ex.Message.Contains("A second operation", StringComparison.Ordinal));

            Assert.NotNull(thrown);
        }
        finally
        {
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public async Task FactoryCreatedDbContext_TwoOperationsWithNoAwaitGap_NeitherThrowsAndBothPersist()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"rel01-factory-{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={dbPath};Foreign Keys=False";
        try
        {
            // Mirrors Program.cs's fixed registration exactly: a factory plus
            // a scoped AppDbContext derived from it.
            var services = new ServiceCollection();
            services.AddSingleton(BuildConfig());
            services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(connStr));
            services.AddScoped<AppDbContext>(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());
            await using var provider = services.BuildServiceProvider(validateScopes: true);

            await using (var seedScope = provider.CreateAsyncScope())
            {
                await seedScope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
            }

            await using var requestScope = provider.CreateAsyncScope();
            var sharedDb = requestScope.ServiceProvider.GetRequiredService<AppDbContext>(); // the request's "_db"
            var factory = requestScope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();

            // Mirrors the FIXED shape: MaybeUpdateCustomInstructionsAsync gets
            // its own factory-created context instead of sharing "_db". Same
            // zero-await-gap firing as the test above -- the tightest overlap
            // that provably throws for a shared context must NOT throw here.
            sharedDb.ChatSessions.Add(new ChatSession { UserId = "user-1", Mode = "dialog", Title = "t" });
            var saveTask = sharedDb.SaveChangesAsync();

            await using var backgroundDb = await factory.CreateDbContextAsync();
            backgroundDb.UserSettings.Add(new UserSettings { UserId = "user-1", CustomSystemPrompt = "remember X" });
            var backgroundSaveTask = backgroundDb.SaveChangesAsync();

            Exception? saveEx = null, backgroundSaveEx = null;
            try { await saveTask; } catch (Exception ex) { saveEx = ex; }
            try { await backgroundSaveTask; } catch (Exception ex) { backgroundSaveEx = ex; }

            Assert.Null(saveEx);
            Assert.Null(backgroundSaveEx);

            await using var verifyDb = await factory.CreateDbContextAsync();
            var sessionCount = await verifyDb.ChatSessions.CountAsync();
            var settingsRow = await verifyDb.UserSettings.FirstOrDefaultAsync(s => s.UserId == "user-1");
            Assert.Equal(1, sessionCount);
            Assert.Equal("remember X", settingsRow?.CustomSystemPrompt);
        }
        finally
        {
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }
}
