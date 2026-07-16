using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VoiceAssistant.API.Data;
using VoiceAssistant.API.Data.Entities;

namespace VoiceAssistant.API.Services;

public enum ReminderInterpretationState
{
    None,
    NeedsClarification,
    Created
}

public record ReminderDraft(
    int Id,
    string Text,
    DateTime DueAtUtc,
    string TimeZoneId,
    string DeviceId,
    string? LocalNotificationId = null,
    string? RecurrenceRule = null)
{
    public bool IsPeriodic => RecurrenceRule is not null;
}
public record ReminderInterpretation(ReminderInterpretationState State, ReminderDraft? Reminder = null);
public record ParsedReminder(bool IsReminder, bool NeedsClarification, string? Text, string? DueAtLocal);
public record ManagedReminder(
    int Id,
    string Text,
    DateTime DueAtUtc,
    string TimeZoneId,
    string? RecurrenceRule,
    string Status);
public record ReminderDeliveryTarget(string DeviceId, string? LocalNotificationId);
public record ReminderCancellationResult(
    int ReminderId,
    string Text,
    IReadOnlyList<ReminderDeliveryTarget> Deliveries);

public class ReminderService
{
    public const int ParseMaxTokens = 450;
    public const int MaxDeviceIdLength = 64;
    public const int MaxReminderTextLength = 500;

    private static readonly Regex ReminderKeyword = new(
        @"\b(напомни|напомнить|напоминание|напоминай|нагадати|нагадай|нагадуй|remind)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DeviceIdPattern = new(
        @"^[A-Za-z0-9._-]{8,64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly GeminiService _gemini;
    private readonly ILogger<ReminderService> _logger;

    public ReminderService(
        IDbContextFactory<AppDbContext> dbFactory,
        GeminiService gemini,
        ILogger<ReminderService> logger)
    {
        _dbFactory = dbFactory;
        _gemini = gemini;
        _logger = logger;
    }

    public static bool MayContainReminder(string message) => ReminderKeyword.IsMatch(message);
    public static bool IsValidDeviceId(string? deviceId) =>
        !string.IsNullOrWhiteSpace(deviceId) && DeviceIdPattern.IsMatch(deviceId);

    public async Task<IReadOnlyList<ManagedReminder>> ListActiveAsync(string userId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Reminders
            .AsNoTracking()
            .Where(item => item.UserId == userId && item.Status == ReminderStatuses.Active)
            .OrderBy(item => item.DueAtUtc)
            .Take(100)
            .Select(item => new ManagedReminder(
                item.Id,
                item.Text,
                item.DueAtUtc,
                item.TimeZoneId,
                item.RecurrenceRule,
                item.Status))
            .ToListAsync(cancellationToken);
    }

    public async Task<ReminderCancellationResult?> CancelAsync(
        string userId,
        int reminderId,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var reminder = await db.Reminders
            .Include(item => item.Deliveries)
            .SingleOrDefaultAsync(item => item.Id == reminderId && item.UserId == userId, cancellationToken);
        if (reminder is null) return null;

        if (reminder.Status == ReminderStatuses.Active)
        {
            reminder.Status = ReminderStatuses.Cancelled;
            reminder.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        return new ReminderCancellationResult(
            reminder.Id,
            reminder.Text,
            reminder.Deliveries
                .Select(delivery => new ReminderDeliveryTarget(delivery.DeviceId, delivery.LocalNotificationId))
                .ToArray());
    }

    public async Task<ReminderInterpretation> TryCreateAsync(
        string userId,
        int sourceMessageId,
        string message,
        string? deviceId,
        string? timeZoneId,
        string? geminiApiKey,
        CancellationToken cancellationToken)
    {
        if (!MayContainReminder(message) || !IsValidDeviceId(deviceId))
            return new ReminderInterpretation(ReminderInterpretationState.None);

        if (!TryResolveTimeZone(timeZoneId, out var timeZone))
            return new ReminderInterpretation(ReminderInterpretationState.NeedsClarification);

        var nowUtc = DateTime.UtcNow;
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, timeZone);
        var prompt = $$"""
            Разбери команду напоминания. Текущее локальное время пользователя:
            {{nowLocal:yyyy-MM-ddTHH:mm:ss}}, часовой пояс: {{timeZone.Id}}.

            Верни только JSON:
            {"isReminder":true,"needsClarification":false,"text":"что напомнить","dueAtLocal":"yyyy-MM-ddTHH:mm:ss"}

            Правила:
            - dueAtLocal — ближайший будущий момент в указанном локальном часовом поясе;
            - text — короткая задача без слов «напомни мне»;
            - если точный день или время нельзя однозначно определить, поставь
              needsClarification=true, а dueAtLocal=null;
            - повторяющиеся напоминания пока требуют уточнения;
            - ничего не додумывай.

            Сообщение: {{message}}
            """;

        try
        {
            var raw = new StringBuilder();
            await foreach (var chunk in _gemini.StreamResponseAsync(
                               "",
                               [new GeminiMessage("user", prompt)],
                               model: "gemini-3.5-flash",
                               maxTokens: ParseMaxTokens,
                               apiKey: geminiApiKey,
                               enableGrounding: false,
                               cancellationToken: cancellationToken))
            {
                raw.Append(chunk);
            }

            var parsed = Parse(raw.ToString());
            if (!parsed.IsReminder) return new ReminderInterpretation(ReminderInterpretationState.None);
            if (parsed.NeedsClarification)
                return new ReminderInterpretation(ReminderInterpretationState.NeedsClarification);

            return await CreateFromToolAsync(
                userId,
                sourceMessageId,
                parsed.Text,
                parsed.DueAtLocal,
                deviceId,
                timeZoneId,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reminder interpretation failed for user {UserId}", userId);
            return new ReminderInterpretation(ReminderInterpretationState.NeedsClarification);
        }
    }

    // Shared persistence/validation path for the legacy parser and the native
    // agent tool. The model chooses the intent and arguments; this service is
    // still the authority for device, time-zone and future-time validation.
    public async Task<ReminderInterpretation> CreateFromToolAsync(
        string userId,
        int sourceMessageId,
        string? text,
        string? dueAtLocal,
        string? deviceId,
        string? timeZoneId,
        CancellationToken cancellationToken,
        Guid? operationId = null)
    {
        if (!IsValidDeviceId(deviceId) || !TryResolveTimeZone(timeZoneId, out var timeZone) ||
            string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(dueAtLocal))
        {
            return new ReminderInterpretation(ReminderInterpretationState.NeedsClarification);
        }

        var nowUtc = DateTime.UtcNow;
        if (!TryConvertToUtc(dueAtLocal, timeZone, out var dueAtUtc) ||
            dueAtUtc <= nowUtc.AddSeconds(10) ||
            dueAtUtc > nowUtc.AddYears(5))
        {
            return new ReminderInterpretation(ReminderInterpretationState.NeedsClarification);
        }

        var normalizedText = NormalizeText(text);
        if (normalizedText.Length == 0)
            return new ReminderInterpretation(ReminderInterpretationState.NeedsClarification);

        return await PersistAsync(
            userId,
            sourceMessageId,
            normalizedText,
            dueAtUtc,
            timeZone.Id,
            deviceId!,
            recurrenceRule: null,
            operationId,
            cancellationToken);
    }

    public async Task<ReminderInterpretation> CreatePeriodicFromToolAsync(
        string userId,
        int sourceMessageId,
        string? text,
        string? startAtLocal,
        string? recurrenceRule,
        string? deviceId,
        string? timeZoneId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        if (!IsValidDeviceId(deviceId) || !TryResolveTimeZone(timeZoneId, out var timeZone) ||
            string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(startAtLocal) ||
            !ReminderRecurrence.TryParse(recurrenceRule, startAtLocal, out var recurrence) ||
            !TryConvertToUtc(startAtLocal, timeZone, out var startAtUtc))
        {
            return new ReminderInterpretation(ReminderInterpretationState.NeedsClarification);
        }

        var nowUtc = DateTime.UtcNow;
        if (startAtUtc <= nowUtc.AddMinutes(1) || startAtUtc > nowUtc.AddYears(5))
            return new ReminderInterpretation(ReminderInterpretationState.NeedsClarification);

        var normalizedText = NormalizeText(text);
        if (normalizedText.Length == 0)
            return new ReminderInterpretation(ReminderInterpretationState.NeedsClarification);

        return await PersistAsync(
            userId,
            sourceMessageId,
            normalizedText,
            startAtUtc,
            timeZone.Id,
            deviceId!,
            recurrence.CanonicalRule,
            operationId,
            cancellationToken);
    }

    private async Task<ReminderInterpretation> PersistAsync(
        string userId,
        int sourceMessageId,
        string normalizedText,
        DateTime dueAtUtc,
        string timeZoneId,
        string deviceId,
        string? recurrenceRule,
        Guid? operationId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var reminders = db.Reminders
                .Include(item => item.Deliveries)
                .Where(item => item.UserId == userId);
            var existing = operationId is { } id
                ? await reminders.FirstOrDefaultAsync(item => item.OperationId == id, cancellationToken)
                : null;
            var replayedOperation = existing is not null;
            existing ??= await reminders.FirstOrDefaultAsync(item =>
                    item.Status == ReminderStatuses.Active &&
                    item.CreatedByDeviceId == deviceId &&
                    item.Text == normalizedText &&
                    item.DueAtUtc == dueAtUtc &&
                    item.RecurrenceRule == recurrenceRule, cancellationToken);
            if (existing is not null)
            {
                // A stable client-turn replay must never resurrect a reminder
                // the user cancelled after the original request completed.
                if (existing.Status != ReminderStatuses.Active)
                    return new ReminderInterpretation(ReminderInterpretationState.NeedsClarification);

                var delivery = existing.Deliveries.SingleOrDefault(item => item.DeviceId == deviceId);
                if (delivery?.Status == ReminderDeliveryStatuses.Cancelled && replayedOperation)
                    return new ReminderInterpretation(ReminderInterpretationState.NeedsClarification);
                if (delivery is null)
                {
                    delivery = new ReminderDelivery { DeviceId = deviceId };
                    existing.Deliveries.Add(delivery);
                }
                else if (delivery.Status is ReminderDeliveryStatuses.Failed or ReminderDeliveryStatuses.Cancelled)
                {
                    delivery.Status = ReminderDeliveryStatuses.Pending;
                    delivery.Error = null;
                    delivery.UpdatedAt = DateTime.UtcNow;
                }

                await db.SaveChangesAsync(cancellationToken);
                return Created(existing, delivery, deviceId);
            }

            var reminder = new Reminder
            {
                UserId = userId,
                Text = normalizedText,
                DueAtUtc = dueAtUtc,
                TimeZoneId = timeZoneId,
                RecurrenceRule = recurrenceRule,
                OperationId = operationId,
                CreatedByDeviceId = deviceId,
                SourceMessageId = sourceMessageId,
                Deliveries =
                [
                    new ReminderDelivery
                    {
                        DeviceId = deviceId,
                        Status = ReminderDeliveryStatuses.Pending
                    }
                ]
            };
            db.Reminders.Add(reminder);
            await db.SaveChangesAsync(cancellationToken);
            return Created(reminder, reminder.Deliveries.Single(), deviceId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DbUpdateException) when (operationId is not null)
        {
            await using var replayDb = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var replay = await replayDb.Reminders
                .AsNoTracking()
                .Include(item => item.Deliveries)
                .FirstOrDefaultAsync(item => item.UserId == userId && item.OperationId == operationId, cancellationToken);
            if (replay is not null && replay.Status == ReminderStatuses.Active)
            {
                var delivery = replay.Deliveries.SingleOrDefault(item => item.DeviceId == deviceId)
                               ?? new ReminderDelivery { DeviceId = deviceId };
                if (delivery.Status == ReminderDeliveryStatuses.Cancelled)
                    return new ReminderInterpretation(ReminderInterpretationState.NeedsClarification);
                return Created(replay, delivery, deviceId);
            }

            return new ReminderInterpretation(ReminderInterpretationState.NeedsClarification);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reminder persistence failed for user {UserId}", userId);
            return new ReminderInterpretation(ReminderInterpretationState.NeedsClarification);
        }
    }

    private static ReminderInterpretation Created(Reminder reminder, ReminderDelivery delivery, string deviceId) =>
        new(
            ReminderInterpretationState.Created,
            new ReminderDraft(
                reminder.Id,
                reminder.Text,
                reminder.DueAtUtc,
                reminder.TimeZoneId,
                deviceId,
                delivery.LocalNotificationId,
                reminder.RecurrenceRule));

    public async Task<string> WaitForDeliveryStatusAsync(
        int reminderId,
        string deviceId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout && !cancellationToken.IsCancellationRequested)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var status = await db.ReminderDeliveries
                .AsNoTracking()
                .Where(delivery => delivery.ReminderId == reminderId && delivery.DeviceId == deviceId)
                .Select(delivery => delivery.Status)
                .SingleOrDefaultAsync(cancellationToken);
            if (status is ReminderDeliveryStatuses.Scheduled or ReminderDeliveryStatuses.Failed)
                return status;

            await Task.Delay(100, cancellationToken);
        }

        return ReminderDeliveryStatuses.Pending;
    }

    public static ParsedReminder Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new ParsedReminder(false, false, null, null);
        var json = raw.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLineEnd = json.IndexOf('\n');
            var lastFence = json.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLineEnd >= 0 && lastFence > firstLineEnd)
                json = json[(firstLineEnd + 1)..lastFence].Trim();
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new ParsedReminder(
                root.TryGetProperty("isReminder", out var isReminder) && isReminder.ValueKind == JsonValueKind.True,
                root.TryGetProperty("needsClarification", out var needsClarification) && needsClarification.ValueKind == JsonValueKind.True,
                root.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null,
                root.TryGetProperty("dueAtLocal", out var dueAt) && dueAt.ValueKind == JsonValueKind.String ? dueAt.GetString() : null);
        }
        catch (JsonException)
        {
            return new ParsedReminder(false, false, null, null);
        }
    }

    public static bool TryConvertToUtc(string localDateTime, TimeZoneInfo timeZone, out DateTime utc)
    {
        utc = default;
        if (!DateTime.TryParseExact(localDateTime, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local))
            return false;

        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(local) || timeZone.IsAmbiguousTime(local)) return false;
        utc = TimeZoneInfo.ConvertTimeToUtc(local, timeZone);
        return true;
    }

    public static bool TryResolveTimeZone(string? id, out TimeZoneInfo timeZone)
    {
        timeZone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100) return false;
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }

    private static string NormalizeText(string value)
    {
        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= MaxReminderTextLength ? normalized : normalized[..MaxReminderTextLength];
    }
}
