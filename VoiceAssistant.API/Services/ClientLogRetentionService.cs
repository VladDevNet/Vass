using Microsoft.EntityFrameworkCore;
using VoiceAssistant.API.Data;

namespace VoiceAssistant.API.Services;

// PROJECT-AUDIT-2026-07-10 DATA-01: client logs (ClientLogsController,
// development-stage debugging tooling for the mobile voice loop) were
// written to Postgres with no TTL/cleanup at all -- they can contain
// transcript fragments, stack traces, or device metadata, and would
// otherwise accumulate forever. 30-day retention, checked once on startup
// (so a real cleanup happens even if the process never stays up a full day
// between deploys) and then every 24h thereafter. Single background
// service is the right scale here -- one VPS, one api instance, same
// rationale as OPS-01's deploy-scoping decision, no distributed job
// scheduler warranted.
//
// Deliberately narrower than the audit's fuller DATA-01 recommendation
// (privacy classification/redaction, feature-flagging remote logging for
// production) -- out of scope per this program's own plan doc, which
// scoped DATA-01 down to "retention policy + scheduled cleanup" only.
public class ClientLogRetentionService : BackgroundService
{
    public static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(30);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ClientLogRetentionService> _logger;

    public ClientLogRetentionService(IServiceScopeFactory scopeFactory, ILogger<ClientLogRetentionService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var deleted = await CleanupExpiredEntriesAsync(db, DateTime.UtcNow, stoppingToken);
                if (deleted > 0)
                {
                    _logger.LogInformation(
                        "Client log retention: deleted {Count} entries older than {RetentionDays} days",
                        deleted, RetentionPeriod.TotalDays);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A transient DB blip here shouldn't crash the whole app (this
                // is a BackgroundService -- an unhandled exception out of
                // ExecuteAsync tears down the host). Log and retry on the next
                // interval instead.
                _logger.LogError(ex, "Client log retention cleanup failed");
            }

            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // Separated from the timer loop so it's directly testable: takes "now"
    // explicitly rather than reading DateTime.UtcNow itself, so tests can
    // seed entries at exact boundary ages without waiting real time.
    public static Task<int> CleanupExpiredEntriesAsync(AppDbContext db, DateTime now, CancellationToken ct)
    {
        var cutoff = now - RetentionPeriod;
        return db.ClientLogEntries.Where(e => e.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
    }
}
