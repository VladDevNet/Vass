using System.Globalization;
using Microsoft.EntityFrameworkCore;
using VoiceAssistant.API.Data;

namespace VoiceAssistant.API.Services;

public sealed record ConversationSearchHit(
    int MessageId,
    int SessionId,
    string Role,
    string Excerpt,
    DateTime CreatedAt);

public sealed record ConversationSearchResult(string Status, IReadOnlyList<ConversationSearchHit> Hits);

// Read-only, owner-scoped lookup for an explicit model tool. It intentionally
// returns short provenance-bearing excerpts rather than a raw session dump.
public sealed class ConversationSearchService
{
    private const int MaxQueryLength = 240;
    private const int MaxTerms = 6;
    private const int MaxHits = 8;
    private const int MaxExcerptLength = 600;
    private const int MaxDateRangeDays = 31;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public ConversationSearchService(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<ConversationSearchResult> SearchAsync(
        string userId,
        string? query,
        string? fromDateLocal,
        string? toDateLocal,
        string? timeZoneId,
        int? excludeMessageId,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeQuery(query);
        var terms = ExtractTerms(normalized);
        if (!TryResolveDateRange(fromDateLocal, toDateLocal, timeZoneId, out var fromUtc, out var toUtc) ||
            (terms.Length == 0 && fromUtc is null))
            return new("invalid", []);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var search = db.Messages.AsNoTracking()
            .Where(message => message.ChatSession.UserId == userId && message.Content != "" &&
                              (!excludeMessageId.HasValue || message.Id != excludeMessageId.Value));

        foreach (var term in terms)
        {
            var capturedTerm = term;
            search = search.Where(message => message.Content.ToLower().Contains(capturedTerm));
        }

        if (fromUtc is { } start && toUtc is { } end)
            search = search.Where(message => message.CreatedAt >= start && message.CreatedAt < end);

        var hits = await search
            .OrderByDescending(message => message.CreatedAt)
            .Take(MaxHits)
            .Select(message => new ConversationSearchHit(
                message.Id,
                message.ChatSessionId,
                message.Role,
                message.Content,
                message.CreatedAt))
            .ToListAsync(cancellationToken);

        var clipped = hits.Select(hit => hit with { Excerpt = Clip(hit.Excerpt) }).ToArray();
        return new(clipped.Length == 0 ? "not_found" : "ok", clipped);
    }

    private static string NormalizeQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return "";
        var normalized = string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= MaxQueryLength ? normalized : normalized[..MaxQueryLength];
    }

    private static string[] ExtractTerms(string query) => query
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Select(token => new string(token.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant())
        .Where(token => token.Length >= 3)
        .Distinct(StringComparer.Ordinal)
        .Take(MaxTerms)
        .ToArray();

    // The model resolves relative language such as "позавчера" from the
    // current local date in its prompt. The broker accepts only a small,
    // explicit local-date interval and converts it before querying UTC data.
    private static bool TryResolveDateRange(
        string? fromDateLocal,
        string? toDateLocal,
        string? timeZoneId,
        out DateTime? fromUtc,
        out DateTime? toUtc)
    {
        fromUtc = null;
        toUtc = null;
        if (string.IsNullOrWhiteSpace(fromDateLocal) && string.IsNullOrWhiteSpace(toDateLocal))
            return true;
        if (string.IsNullOrWhiteSpace(fromDateLocal) || string.IsNullOrWhiteSpace(toDateLocal) ||
            !ReminderService.TryResolveTimeZone(timeZoneId, out var timeZone) ||
            !DateOnly.TryParseExact(fromDateLocal, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fromDate) ||
            !DateOnly.TryParseExact(toDateLocal, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var toDate) ||
            toDate < fromDate || toDate == DateOnly.MaxValue ||
            toDate.DayNumber - fromDate.DayNumber >= MaxDateRangeDays)
        {
            return false;
        }

        var localStart = DateTime.SpecifyKind(fromDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        var localEnd = DateTime.SpecifyKind(toDate.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(localStart) || timeZone.IsAmbiguousTime(localStart) ||
            timeZone.IsInvalidTime(localEnd) || timeZone.IsAmbiguousTime(localEnd))
        {
            return false;
        }

        fromUtc = TimeZoneInfo.ConvertTimeToUtc(localStart, timeZone);
        toUtc = TimeZoneInfo.ConvertTimeToUtc(localEnd, timeZone);
        return true;
    }

    private static string Clip(string value) => value.Length <= MaxExcerptLength
        ? value
        : value[..MaxExcerptLength] + "...";
}
