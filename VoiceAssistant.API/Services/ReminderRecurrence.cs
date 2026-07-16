using System.Globalization;

namespace VoiceAssistant.API.Services;

public sealed record ReminderRecurrenceRule(
    string CanonicalRule,
    string Frequency,
    int Interval,
    string? ByDay = null);

// A deliberately small RFC 5545 RRULE subset that maps one-to-one to the
// cross-platform triggers exposed by expo-notifications. Unsupported RRULE
// parts are rejected instead of being silently ignored.
public static class ReminderRecurrence
{
    public const int MaxRuleLength = 200;
    // Android may throttle exact alarms in Doze to roughly one per nine
    // minutes. Fifteen minutes is the smallest cross-platform interval V1
    // advertises without knowingly promising a cadence the OS cannot keep.
    public const int MinimumMinuteInterval = 15;
    public const int MaximumMinuteInterval = 10_080;
    public const int MaximumHourInterval = 168;

    private static readonly IReadOnlyDictionary<string, DayOfWeek> Weekdays =
        new Dictionary<string, DayOfWeek>(StringComparer.Ordinal)
        {
            ["SU"] = DayOfWeek.Sunday,
            ["MO"] = DayOfWeek.Monday,
            ["TU"] = DayOfWeek.Tuesday,
            ["WE"] = DayOfWeek.Wednesday,
            ["TH"] = DayOfWeek.Thursday,
            ["FR"] = DayOfWeek.Friday,
            ["SA"] = DayOfWeek.Saturday
        };

    public static bool TryParse(
        string? rawRule,
        string? startAtLocal,
        out ReminderRecurrenceRule rule)
    {
        rule = null!;
        if (string.IsNullOrWhiteSpace(rawRule) || rawRule.Length > MaxRuleLength ||
            !DateTime.TryParseExact(startAtLocal, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var localStart) || localStart.Second != 0)
        {
            return false;
        }

        var value = rawRule.Trim().ToUpperInvariant();
        if (value.StartsWith("RRULE:", StringComparison.Ordinal)) value = value[6..];
        if (value.Length == 0 || value.Any(char.IsWhiteSpace)) return false;

        var rawParts = value.Split(';');
        if (rawParts.Any(string.IsNullOrEmpty)) return false;

        var parts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in rawParts)
        {
            var separator = item.IndexOf('=');
            if (separator <= 0 || separator == item.Length - 1) return false;
            var key = item[..separator];
            var itemValue = item[(separator + 1)..];
            if (!parts.TryAdd(key, itemValue)) return false;
        }

        if (!parts.TryGetValue("FREQ", out var frequency)) return false;
        if (!TryReadInterval(parts, out var interval)) return false;

        return frequency switch
        {
            "DAILY" => TryDaily(parts, interval, out rule),
            "WEEKLY" => TryWeekly(parts, interval, localStart, out rule),
            "MONTHLY" => TryMonthly(parts, interval, localStart, out rule),
            "YEARLY" => TryYearly(parts, interval, localStart, out rule),
            "HOURLY" => TryInterval(parts, frequency, interval, 1, MaximumHourInterval, out rule),
            "MINUTELY" => TryInterval(parts, frequency, interval, MinimumMinuteInterval, MaximumMinuteInterval, out rule),
            _ => false
        };
    }

    private static bool TryDaily(
        IReadOnlyDictionary<string, string> parts,
        int interval,
        out ReminderRecurrenceRule rule)
    {
        rule = null!;
        if (interval != 1 || !HasOnly(parts, "FREQ", "INTERVAL")) return false;
        rule = new ReminderRecurrenceRule("FREQ=DAILY", "DAILY", 1);
        return true;
    }

    private static bool TryWeekly(
        IReadOnlyDictionary<string, string> parts,
        int interval,
        DateTime localStart,
        out ReminderRecurrenceRule rule)
    {
        rule = null!;
        if (interval != 1 || !HasOnly(parts, "FREQ", "INTERVAL", "BYDAY") ||
            !parts.TryGetValue("BYDAY", out var byDay) || byDay.Contains(',') ||
            !Weekdays.TryGetValue(byDay, out var weekday) || weekday != localStart.DayOfWeek)
        {
            return false;
        }

        rule = new ReminderRecurrenceRule($"FREQ=WEEKLY;BYDAY={byDay}", "WEEKLY", 1, byDay);
        return true;
    }

    private static bool TryMonthly(
        IReadOnlyDictionary<string, string> parts,
        int interval,
        DateTime localStart,
        out ReminderRecurrenceRule rule)
    {
        rule = null!;
        if (interval != 1 || localStart.Day > 28 ||
            !HasOnly(parts, "FREQ", "INTERVAL", "BYMONTHDAY")) return false;
        if (parts.TryGetValue("BYMONTHDAY", out var rawDay) &&
            (!int.TryParse(rawDay, NumberStyles.None, CultureInfo.InvariantCulture, out var day) || day != localStart.Day))
        {
            return false;
        }

        rule = new ReminderRecurrenceRule($"FREQ=MONTHLY;BYMONTHDAY={localStart.Day}", "MONTHLY", 1);
        return true;
    }

    private static bool TryYearly(
        IReadOnlyDictionary<string, string> parts,
        int interval,
        DateTime localStart,
        out ReminderRecurrenceRule rule)
    {
        rule = null!;
        if (interval != 1 || (localStart.Month == 2 && localStart.Day == 29) ||
            !HasOnly(parts, "FREQ", "INTERVAL", "BYMONTH", "BYMONTHDAY")) return false;
        if (parts.TryGetValue("BYMONTH", out var rawMonth) &&
            (!int.TryParse(rawMonth, NumberStyles.None, CultureInfo.InvariantCulture, out var month) || month != localStart.Month))
        {
            return false;
        }
        if (parts.TryGetValue("BYMONTHDAY", out var rawDay) &&
            (!int.TryParse(rawDay, NumberStyles.None, CultureInfo.InvariantCulture, out var day) || day != localStart.Day))
        {
            return false;
        }

        rule = new ReminderRecurrenceRule(
            $"FREQ=YEARLY;BYMONTH={localStart.Month};BYMONTHDAY={localStart.Day}",
            "YEARLY",
            1);
        return true;
    }

    private static bool TryInterval(
        IReadOnlyDictionary<string, string> parts,
        string frequency,
        int interval,
        int minimum,
        int maximum,
        out ReminderRecurrenceRule rule)
    {
        rule = null!;
        if (!HasOnly(parts, "FREQ", "INTERVAL") || interval < minimum || interval > maximum)
            return false;
        rule = new ReminderRecurrenceRule($"FREQ={frequency};INTERVAL={interval}", frequency, interval);
        return true;
    }

    private static bool TryReadInterval(IReadOnlyDictionary<string, string> parts, out int interval)
    {
        interval = 1;
        return !parts.TryGetValue("INTERVAL", out var raw) ||
               int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out interval) && interval > 0;
    }

    private static bool HasOnly(IReadOnlyDictionary<string, string> parts, params string[] allowed)
    {
        var set = allowed.ToHashSet(StringComparer.Ordinal);
        return parts.Keys.All(set.Contains);
    }
}
