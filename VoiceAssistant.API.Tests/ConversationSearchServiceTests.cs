using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using VoiceAssistant.API.Data;
using VoiceAssistant.API.Data.Entities;
using VoiceAssistant.API.Services;

namespace VoiceAssistant.API.Tests;

public class ConversationSearchServiceTests
{
    [Fact]
    public async Task SearchAsync_UsesOwnerScopedInclusiveLocalDateRangeAndExcludesCurrentMessage()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"conversation-search-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={dbPath};Foreign Keys=False")
                .Options;
            var configuration = BuildConfiguration();
            int currentMessageId;

            await using (var db = new AppDbContext(options, configuration))
            {
                await db.Database.EnsureCreatedAsync();
                var ownerSession = new ChatSession { UserId = "owner", Title = "Owner" };
                var otherSession = new ChatSession { UserId = "other", Title = "Other" };
                var current = new Message
                {
                    ChatSession = ownerSession,
                    Role = "user",
                    Content = "Текущий запрос о космонавте",
                    CreatedAt = new DateTime(2026, 7, 14, 15, 0, 0, DateTimeKind.Utc)
                };
                db.Messages.AddRange(
                    new Message
                    {
                        ChatSession = ownerSession,
                        Role = "user",
                        Content = "Хочу стать космонавтом",
                        CreatedAt = new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc)
                    },
                    current,
                    new Message
                    {
                        ChatSession = ownerSession,
                        Role = "assistant",
                        Content = "Обсудили планы на завтра",
                        CreatedAt = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc)
                    },
                    new Message
                    {
                        ChatSession = otherSession,
                        Role = "user",
                        Content = "Чужой разговор о космонавте",
                        CreatedAt = new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc)
                    });
                await db.SaveChangesAsync();
                currentMessageId = current.Id;
            }

            var service = new ConversationSearchService(new TestDbContextFactory(options, configuration));
            var result = await service.SearchAsync(
                "owner",
                "космонавт",
                "2026-07-14",
                "2026-07-14",
                TimeZoneInfo.Utc.Id,
                currentMessageId,
                CancellationToken.None);

            Assert.Equal("ok", result.Status);
            var hit = Assert.Single(result.Hits);
            Assert.Equal("Хочу стать космонавтом", hit.Excerpt);

            var invalid = await service.SearchAsync(
                "owner",
                null,
                "2026-07-14",
                null,
                TimeZoneInfo.Utc.Id,
                null,
                CancellationToken.None);
            Assert.Equal("invalid", invalid.Status);
        }
        finally
        {
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }

    private static IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Encryption:Key"] = "conversation-search-test-key-0123456789",
            })
            .Build();

    private sealed class TestDbContextFactory(
        DbContextOptions<AppDbContext> options,
        IConfiguration configuration) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options, configuration);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
