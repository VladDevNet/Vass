using Microsoft.EntityFrameworkCore;
using VoiceAssistant.API.Data;
using VoiceAssistant.API.Data.Entities;

namespace VoiceAssistant.API.Services;

public sealed record CapabilityDiscoveryItem(
    string Id,
    string Title,
    string Description,
    IReadOnlyList<string> Examples,
    string InterfaceHint,
    string State,
    int UsageCount,
    DateTime? FirstUsedAt,
    DateTime? LastUsedAt,
    int SuggestionCount,
    DateTime? LastSuggestedAt,
    DateTime? DeclinedAt,
    DateTime? PendingResponseSeenAt);

public sealed record CapabilityDiscoverySnapshot(
    bool CanSuggest,
    int UserMessageCount,
    IReadOnlyList<CapabilityDiscoveryItem> Items);

public sealed record CapabilityDiscoveryCandidate(
    string Id,
    string Title,
    string Description,
    IReadOnlyList<string> Examples,
    string InterfaceHint);

public sealed record CapabilityDiscoveryToolResult(
    string Status,
    string Summary,
    CapabilityDiscoveryCandidate? Capability = null,
    IReadOnlyList<CapabilityDiscoveryCandidate>? Candidates = null);

public sealed record CapabilityDiscoveryTurnContext(bool RequiresAgentPlanner, string? Prompt);

// Chooses when an optional capability hint may be considered. The model still
// decides relevance; this service only supplies a bounded, privacy-safe
// candidate set and persists the user's explicit preference.
public sealed class CapabilityDiscoveryService
{
    private const int MinimumUserMessages = 4;
    private const int MaximumSuggestionsPerCapability = 2;
    private static readonly TimeSpan GlobalSuggestionCooldown = TimeSpan.FromDays(7);
    private static readonly TimeSpan CapabilitySuggestionCooldown = TimeSpan.FromDays(28);
    private static readonly TimeSpan PendingResponseWindow = TimeSpan.FromDays(7);

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AssistantCapabilityRegistry _capabilities;

    public CapabilityDiscoveryService(
        IDbContextFactory<AppDbContext> dbFactory,
        AssistantCapabilityRegistry capabilities)
    {
        _dbFactory = dbFactory;
        _capabilities = capabilities;
    }

    public async Task<CapabilityDiscoverySnapshot> GetSnapshotAsync(
        string userId,
        AssistantRuntimeContext context,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var available = _capabilities.GetDiscoverableHelp(context);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var progress = await db.CapabilityDiscoveryProgresses
            .Where(item => item.UserId == userId)
            .ToListAsync(cancellationToken);
        var byCapability = progress.ToDictionary(item => item.CapabilityId, StringComparer.Ordinal);
        var userMessageCount = await db.Messages
            .CountAsync(message => message.Role == "user" && message.ChatSession.UserId == userId, cancellationToken);
        var lastConsideredAt = await db.Users
            .Where(user => user.Id == userId)
            .Select(user => user.LastCapabilityDiscoveryConsideredAt)
            .SingleOrDefaultAsync(cancellationToken);
        var items = available
            .Select(help => ToItem(help, byCapability.GetValueOrDefault(help.Id)))
            .OrderBy(item => DiscoveryPriority(item.Id))
            .ToArray();
        var lastSuggestionAt = progress
            .Where(item => item.LastSuggestedAt is not null)
            .Select(item => item.LastSuggestedAt)
            .Max();
        var canSuggest = userMessageCount >= MinimumUserMessages &&
                         (lastSuggestionAt is null || now - lastSuggestionAt.Value >= GlobalSuggestionCooldown) &&
                         (lastConsideredAt is null || now - lastConsideredAt.Value >= GlobalSuggestionCooldown) &&
                         items.Any(item => IsEligibleForSuggestion(item, now));
        return new(canSuggest, userMessageCount, items);
    }

    public async Task<CapabilityDiscoveryTurnContext> GetTurnContextAsync(
        string userId,
        AssistantRuntimeContext context,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var snapshot = await GetSnapshotAsync(userId, context, cancellationToken);
        var pending = snapshot.Items
            .Where(item => item.UsageCount == 0 && item.DeclinedAt is null && item.LastSuggestedAt is not null &&
                           item.PendingResponseSeenAt is null && now - item.LastSuggestedAt.Value <= PendingResponseWindow)
            .OrderByDescending(item => item.LastSuggestedAt)
            .FirstOrDefault();
        if (pending is not null)
        {
            await MarkPendingResponseSeenAsync(userId, pending.Id, cancellationToken);
            return new(true, $$"""
                ## Ненавязчивые подсказки возможностей
                В предыдущем ответе пользователю уже была ненавязчиво предложена возможность
                `{{pending.Id}}` («{{pending.Title}}»). Если текущая реплика ЯВНО означает
                отказ от этой конкретной подсказки (например, «не хочу», «не предлагай это»),
                вызови capability_discovery_decline с capabilityId `{{pending.Id}}` до обычного
                ответа. Не считай отказом смену темы, молчаливое игнорирование или уточняющий
                вопрос. Не предлагай эту возможность повторно в этом ходе.
                """);
        }

        if (!snapshot.CanSuggest)
            return new(false, null);

        await MarkOpportunityConsideredAsync(userId, cancellationToken);

        return new(true, """
            ## Ненавязчивые подсказки возможностей
            В этом ходе МОЖНО, но не обязательно, познакомить пользователя максимум с одной
            ещё не использованной возможностью. Сначала полностью реши его текущую задачу.
            Только если одна возможность прямо поможет в контексте разговора, вызови
            capability_discovery_candidates, выбери ровно одну и затем вызови
            capability_discovery_present. После успешного present добавь в самом конце ответа
            одну короткую естественную фразу-предложение без давления. Не прерывай ответ,
            не делай подсказку при ошибке, срочном/личном вопросе, явной команде, обычном
            приветствии или если релевантной возможности нет. Никогда не упоминай новую
            возможность без успешного capability_discovery_present.
            """);
    }

    public async Task<CapabilityDiscoveryToolResult> GetCandidatesAsync(
        string userId,
        AssistantRuntimeContext context,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var snapshot = await GetSnapshotAsync(userId, context, cancellationToken);
        if (!await HasRecentOpportunityAsync(userId, now, cancellationToken))
            return new("unavailable", "Сейчас новую подсказку предлагать не нужно.", Candidates: []);

        var candidates = snapshot.Items
            .Where(item => IsEligibleForSuggestion(item, now))
            .OrderBy(item => DiscoveryPriority(item.Id))
            .Take(4)
            .Select(ToCandidate)
            .ToArray();
        return new("ok",
            candidates.Length == 0
                ? "Сейчас подходящих новых возможностей нет."
                : "Можно выбрать максимум одну возможность, только если она напрямую полезна для текущего разговора.",
            Candidates: candidates);
    }

    public async Task<CapabilityDiscoveryToolResult> PresentAsync(
        string userId,
        string? capabilityId,
        AssistantRuntimeContext context,
        CancellationToken cancellationToken)
    {
        var normalizedId = NormalizeCapabilityId(capabilityId);
        if (normalizedId is null)
            return new("invalid", "Нужен стабильный capabilityId из capability_discovery_candidates.");

        var now = DateTime.UtcNow;
        var snapshot = await GetSnapshotAsync(userId, context, cancellationToken);
        var item = snapshot.Items.SingleOrDefault(candidate => candidate.Id == normalizedId);
        var allowedCandidates = snapshot.Items
            .Where(candidate => IsEligibleForSuggestion(candidate, now))
            .OrderBy(candidate => DiscoveryPriority(candidate.Id))
            .Take(4)
            .ToArray();
        if (item is null ||
            !allowedCandidates.Any(candidate => candidate.Id == normalizedId) ||
            !await HasRecentOpportunityAsync(userId, now, cancellationToken))
            return new("unavailable", "Эту возможность сейчас не нужно предлагать.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var progress = await GetOrCreateAsync(db, userId, normalizedId, cancellationToken);
        progress.SuggestionCount++;
        progress.FirstSuggestedAt ??= now;
        progress.LastSuggestedAt = now;
        progress.PendingResponseSeenAt = null;
        progress.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        var candidate = ToCandidate(item);
        return new("presented",
            $"Подсказка для «{candidate.Title}» зафиксирована. После основного ответа можно дать одну короткую, необязательную фразу с примером.",
            Capability: candidate);
    }

    public async Task<CapabilityDiscoveryToolResult> DeclineAsync(
        string userId,
        string? capabilityId,
        AssistantRuntimeContext context,
        CancellationToken cancellationToken)
    {
        var normalizedId = NormalizeCapabilityId(capabilityId);
        var help = normalizedId is null
            ? null
            : _capabilities.GetDiscoverableHelp(context).SingleOrDefault(item => item.Id == normalizedId);
        if (help is null)
            return new("invalid", "Можно отключить только известную доступную возможность из каталога.");

        var now = DateTime.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var progress = await db.CapabilityDiscoveryProgresses.SingleOrDefaultAsync(
            item => item.UserId == userId && item.CapabilityId == normalizedId,
            cancellationToken);
        if (progress?.LastSuggestedAt is null)
            return new("unavailable", "Отключить можно только подсказку, которая уже была действительно показана.");
        progress.DeclinedAt ??= now;
        progress.PendingResponseSeenAt ??= now;
        progress.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return new("declined", $"Подсказки для «{help.Title}» отключены для этого пользователя.", ToCandidate(ToItem(help, progress)));
    }

    public async Task<bool> MarkClientReportedUseAsync(string userId, string? capabilityId, CancellationToken cancellationToken)
    {
        var normalizedId = NormalizeCapabilityId(capabilityId);
        if (normalizedId is not ("share" or "screen" or "overlay")) return false;
        await MarkUsedAsync(userId, normalizedId, cancellationToken);
        return true;
    }

    public async Task MarkUsedAsync(string userId, string capabilityId, CancellationToken cancellationToken)
    {
        if (!AssistantCapabilityRegistry.IsDiscoverableHelpId(capabilityId)) return;

        var now = DateTime.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var progress = await GetOrCreateAsync(db, userId, capabilityId, cancellationToken);
        progress.UsageCount++;
        progress.FirstUsedAt ??= now;
        progress.LastUsedAt = now;
        progress.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkPendingResponseSeenAsync(string userId, string capabilityId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var progress = await db.CapabilityDiscoveryProgresses.SingleOrDefaultAsync(
            item => item.UserId == userId && item.CapabilityId == capabilityId,
            cancellationToken);
        if (progress is null || progress.PendingResponseSeenAt is not null) return;
        progress.PendingResponseSeenAt = DateTime.UtcNow;
        progress.UpdatedAt = progress.PendingResponseSeenAt.Value;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkOpportunityConsideredAsync(string userId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.SingleOrDefaultAsync(item => item.Id == userId, cancellationToken);
        if (user is null) return;
        user.LastCapabilityDiscoveryConsideredAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> HasRecentOpportunityAsync(
        string userId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var minimumConsideredAt = now - TimeSpan.FromMinutes(5);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Users
            .Where(user => user.Id == userId)
            .Select(user => user.LastCapabilityDiscoveryConsideredAt)
            .AnyAsync(value => value != null && value >= minimumConsideredAt, cancellationToken);
    }

    private static async Task<CapabilityDiscoveryProgress> GetOrCreateAsync(
        AppDbContext db,
        string userId,
        string capabilityId,
        CancellationToken cancellationToken)
    {
        var progress = await db.CapabilityDiscoveryProgresses.SingleOrDefaultAsync(
            item => item.UserId == userId && item.CapabilityId == capabilityId,
            cancellationToken);
        if (progress is not null) return progress;

        progress = new CapabilityDiscoveryProgress
        {
            UserId = userId,
            CapabilityId = capabilityId,
        };
        db.CapabilityDiscoveryProgresses.Add(progress);
        return progress;
    }

    private static CapabilityDiscoveryItem ToItem(AssistantCapabilityHelpItem help, CapabilityDiscoveryProgress? progress) => new(
        help.Id,
        help.Title,
        help.Description,
        help.Examples,
        help.InterfaceHint,
        progress?.DeclinedAt is not null ? "declined" : progress?.UsageCount > 0 ? "used" : "unused",
        progress?.UsageCount ?? 0,
        progress?.FirstUsedAt,
        progress?.LastUsedAt,
        progress?.SuggestionCount ?? 0,
        progress?.LastSuggestedAt,
        progress?.DeclinedAt,
        progress?.PendingResponseSeenAt);

    private static CapabilityDiscoveryCandidate ToCandidate(CapabilityDiscoveryItem item) => new(
        item.Id,
        item.Title,
        item.Description,
        item.Examples,
        item.InterfaceHint);

    private static bool IsEligibleForSuggestion(CapabilityDiscoveryItem item, DateTime now) =>
        item.UsageCount == 0 &&
        item.DeclinedAt is null &&
        item.SuggestionCount < MaximumSuggestionsPerCapability &&
        (item.LastSuggestedAt is null || now - item.LastSuggestedAt.Value >= CapabilitySuggestionCooldown);

    private static int DiscoveryPriority(string capabilityId) => capabilityId switch
    {
        "memory" => 0,
        "reminders" => 1,
        "visual" => 2,
        "share" => 3,
        "screen" => 4,
        "library" => 5,
        "youtube" => 6,
        "overlay" => 7,
        _ => 99,
    };

    private static string? NormalizeCapabilityId(string? capabilityId)
    {
        if (string.IsNullOrWhiteSpace(capabilityId)) return null;
        var value = capabilityId.Trim().ToLowerInvariant();
        return AssistantCapabilityRegistry.IsDiscoverableHelpId(value) ? value : null;
    }
}
