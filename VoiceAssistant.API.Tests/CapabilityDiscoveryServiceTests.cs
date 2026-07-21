using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using VoiceAssistant.API.Data;
using VoiceAssistant.API.Data.Entities;
using VoiceAssistant.API.Services;

namespace VoiceAssistant.API.Tests;

public class CapabilityDiscoveryServiceTests
{
    [Fact]
    public async Task Candidates_ExcludeUsedCapabilities_AndDeclineStopsFutureSuggestions()
    {
        await using var fixture = await DiscoveryFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.MarkUsedAsync(fixture.UserId, "memory", CancellationToken.None);
        var turn = await service.GetTurnContextAsync(fixture.UserId, fixture.Context, CancellationToken.None);
        var candidates = await service.GetCandidatesAsync(fixture.UserId, fixture.Context, CancellationToken.None);

        Assert.True(turn.RequiresAgentPlanner);
        Assert.Equal("ok", candidates.Status);
        Assert.DoesNotContain(candidates.Candidates!, item => item.Id == "memory");
        var reminder = Assert.Single(candidates.Candidates!, item => item.Id == "reminders");

        var presented = await service.PresentAsync(fixture.UserId, reminder.Id, fixture.Context, CancellationToken.None);
        Assert.Equal("presented", presented.Status);

        var declined = await service.DeclineAsync(fixture.UserId, reminder.Id, fixture.Context, CancellationToken.None);
        Assert.Equal("declined", declined.Status);

        var snapshot = await service.GetSnapshotAsync(fixture.UserId, fixture.Context, CancellationToken.None);
        Assert.Equal("used", Assert.Single(snapshot.Items, item => item.Id == "memory").State);
        Assert.Equal("declined", Assert.Single(snapshot.Items, item => item.Id == "reminders").State);
    }

    [Fact]
    public async Task Present_RequiresOneOfTheJustReturnedCandidates()
    {
        await using var fixture = await DiscoveryFixture.CreateAsync();
        var service = fixture.CreateService();

        var turn = await service.GetTurnContextAsync(fixture.UserId, fixture.Context, CancellationToken.None);
        var candidates = await service.GetCandidatesAsync(fixture.UserId, fixture.Context, CancellationToken.None);
        Assert.True(turn.RequiresAgentPlanner);
        Assert.Equal("ok", candidates.Status);
        Assert.DoesNotContain(candidates.Candidates!, item => item.Id == "youtube");

        var result = await service.PresentAsync(fixture.UserId, "youtube", fixture.Context, CancellationToken.None);

        Assert.Equal("unavailable", result.Status);
    }

    [Fact]
    public async Task Candidates_RequireOneRareDiscoveryWindow()
    {
        await using var fixture = await DiscoveryFixture.CreateAsync();
        var service = fixture.CreateService();

        var withoutWindow = await service.GetCandidatesAsync(fixture.UserId, fixture.Context, CancellationToken.None);
        var firstWindow = await service.GetTurnContextAsync(fixture.UserId, fixture.Context, CancellationToken.None);
        var secondWindow = await service.GetTurnContextAsync(fixture.UserId, fixture.Context, CancellationToken.None);

        Assert.Equal("unavailable", withoutWindow.Status);
        Assert.True(firstWindow.RequiresAgentPlanner);
        Assert.False(secondWindow.RequiresAgentPlanner);
    }

    [Fact]
    public async Task Decline_RequiresAnActuallyPresentedCapability()
    {
        await using var fixture = await DiscoveryFixture.CreateAsync();
        var service = fixture.CreateService();

        var result = await service.DeclineAsync(fixture.UserId, "screen", fixture.Context, CancellationToken.None);
        var snapshot = await service.GetSnapshotAsync(fixture.UserId, fixture.Context, CancellationToken.None);

        Assert.Equal("unavailable", result.Status);
        Assert.Equal("unused", Assert.Single(snapshot.Items, item => item.Id == "screen").State);
    }

    [Fact]
    public async Task ClientUsage_AcceptsOnlyClientOwnedCapabilitySignals()
    {
        await using var fixture = await DiscoveryFixture.CreateAsync();
        var service = fixture.CreateService();

        Assert.True(await service.MarkClientReportedUseAsync(fixture.UserId, "share", CancellationToken.None));
        Assert.False(await service.MarkClientReportedUseAsync(fixture.UserId, "memory", CancellationToken.None));

        var snapshot = await service.GetSnapshotAsync(fixture.UserId, fixture.Context, CancellationToken.None);
        var share = Assert.Single(snapshot.Items, item => item.Id == "share");
        Assert.Equal("used", share.State);
        Assert.Equal(1, share.UsageCount);
    }

    private sealed class DiscoveryFixture : IAsyncDisposable
    {
        private readonly string _dbPath;
        private readonly DbContextOptions<AppDbContext> _options;
        private readonly IConfiguration _configuration;

        private const string OwnerId = "capability-owner";
        public string UserId => OwnerId;
        public AssistantRuntimeContext Context { get; } = new(
            HasVisualAttachment: false,
            SupportsScreenAnalysis: true,
            SupportsExternalActions: true,
            SupportsReminders: true,
            SupportsPeriodicReminders: true,
            SupportsLibrary: true);

        private DiscoveryFixture(string dbPath, DbContextOptions<AppDbContext> options, IConfiguration configuration)
        {
            _dbPath = dbPath;
            _options = options;
            _configuration = configuration;
        }

        public static async Task<DiscoveryFixture> CreateAsync()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"capability-discovery-{Guid.NewGuid():N}.db");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Encryption:Key"] = "capability-discovery-test-key-0123456789",
                })
                .Build();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={dbPath};Foreign Keys=False")
                .Options;
            var fixture = new DiscoveryFixture(dbPath, options, configuration);

            await using var db = new AppDbContext(options, configuration);
            await db.Database.EnsureCreatedAsync();
            var user = new User
            {
                Id = OwnerId,
                UserName = "capability-owner",
                NormalizedUserName = "CAPABILITY-OWNER",
                Email = "capability-owner@example.test",
                NormalizedEmail = "CAPABILITY-OWNER@EXAMPLE.TEST",
            };
            var session = new ChatSession { User = user, UserId = user.Id, Title = "Discovery" };
            db.Users.Add(user);
            db.Messages.AddRange(Enumerable.Range(1, 4).Select(index => new Message
            {
                ChatSession = session,
                Role = "user",
                Content = $"Сообщение {index}",
            }));
            await db.SaveChangesAsync();
            return fixture;
        }

        public CapabilityDiscoveryService CreateService() =>
            new(new TestDbContextFactory(_options, _configuration), new AssistantCapabilityRegistry(new ConfigurationBuilder().Build()));

        public ValueTask DisposeAsync()
        {
            try
            {
                if (File.Exists(_dbPath)) File.Delete(_dbPath);
            }
            catch
            {
                // A failed cleanup does not affect the isolated temporary test database.
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestDbContextFactory(
        DbContextOptions<AppDbContext> options,
        IConfiguration configuration) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options, configuration);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
