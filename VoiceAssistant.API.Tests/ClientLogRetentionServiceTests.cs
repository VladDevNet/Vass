using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using VoiceAssistant.API.Data;
using VoiceAssistant.API.Data.Entities;
using VoiceAssistant.API.Services;
using Xunit;

namespace VoiceAssistant.API.Tests;

// PROJECT-AUDIT-2026-07-10 DATA-01: direct coverage of the retention
// cutoff logic, independent of ClientLogRetentionService's own 24h timer
// loop (not itself worth testing -- the scheduling is framework-provided
// BackgroundService machinery, not custom logic).
public class ClientLogRetentionServiceTests
{
    private static IConfiguration BuildConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Encryption:Key"] = "test-harness-not-a-real-secret-0123456789",
            })
            .Build();

    [Fact]
    public async Task CleanupExpiredEntriesAsync_DeletesOnlyEntriesOlderThanRetentionPeriod()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"data01-retention-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={dbPath};Foreign Keys=False")
                .Options;

            var now = new DateTime(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc);

            await using (var seedDb = new AppDbContext(options, BuildConfig()))
            {
                await seedDb.Database.EnsureCreatedAsync();

                seedDb.ClientLogEntries.AddRange(
                    NewEntry("too-old", now - TimeSpan.FromDays(31)),
                    // Exactly at the retention boundary -- CleanupExpiredEntriesAsync
                    // uses CreatedAt < cutoff (strictly less than), so an entry created
                    // EXACTLY RetentionPeriod ago is still within its 30th day and must
                    // survive; only entries older than that are removed.
                    NewEntry("exactly-at-boundary", now - ClientLogRetentionService.RetentionPeriod),
                    NewEntry("recent", now - TimeSpan.FromDays(5)));
                await seedDb.SaveChangesAsync();
            }

            int deletedCount;
            await using (var db = new AppDbContext(options, BuildConfig()))
            {
                deletedCount = await ClientLogRetentionService.CleanupExpiredEntriesAsync(db, now, CancellationToken.None);
            }
            Assert.Equal(1, deletedCount);

            await using (var verifyDb = new AppDbContext(options, BuildConfig()))
            {
                var remaining = await verifyDb.ClientLogEntries.Select(e => e.RunId).ToListAsync();
                Assert.Equal(["exactly-at-boundary", "recent"], remaining.OrderBy(r => r).ToArray());
            }
        }
        finally
        {
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }

    private static ClientLogEntry NewEntry(string runId, DateTime createdAt) => new()
    {
        UserId = "test-user",
        CreatedAt = createdAt,
        ClientTimestamp = createdAt,
        RunId = runId,
        Level = "info",
        Category = "app",
        Message = "test message",
    };
}
