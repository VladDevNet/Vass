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
    string? LocalNotificationId = null);
public record ReminderInterpretation(ReminderInterpretationState State, ReminderDraft? Reminder = null);
public record ParsedReminder(bool IsReminder, bool NeedsClarification, string? Text, string? DueAtLocal);

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
        CancellationToken cancellationToken)
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

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var existing = await db.Reminders
                .Include(item => item.Deliveries)
                .Where(item => item.UserId == userId &&
                               item.Status == ReminderStatuses.Active &&
                               item.CreatedByDeviceId == deviceId &&
                               item.Text == normalizedText &&
                               item.DueAtUtc == dueAtUtc)
                .FirstOrDefaultAsync(cancellationToken);
            if (existing is not null)
            {
                var delivery = existing.Deliveries.Single(item => item.DeviceId == deviceId);
                if (delivery.Status == ReminderDeliveryStatuses.Failed)
                {
                    delivery.Status = ReminderDeliveryStatuses.Pending;
                    delivery.Error = null;
                    delivery.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);
                }

                return new ReminderInterpretation(
                    ReminderInterpretationState.Created,
                    new ReminderDraft(
                        existing.Id,
                        existing.Text,
                        existing.DueAtUtc,
                        existing.TimeZoneId,
                        deviceId!,
                        delivery.LocalNotificationId));
            }

            var reminder = new Reminder
            {
                UserId = userId,
                Text = normalizedText,
                DueAtUtc = dueAtUtc,
                TimeZoneId = timeZone.Id,
                CreatedByDeviceId = deviceId!,
                SourceMessageId = sourceMessageId,
                Deliveries =
                [
                    new ReminderDelivery
                    {
                        DeviceId = deviceId!,
                        Status = ReminderDeliveryStatuses.Pending
                    }
                ]
            };
            db.Reminders.Add(reminder);
            await db.SaveChangesAsync(cancellationToken);

            return new ReminderInterpretation(
                ReminderInterpretationState.Created,
                new ReminderDraft(reminder.Id, reminder.Text, reminder.DueAtUtc, reminder.TimeZoneId, deviceId!));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reminder persistence failed for user {UserId}", userId);
            return new ReminderInterpretation(ReminderInterpretationState.NeedsClarification);
        }
    }

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
