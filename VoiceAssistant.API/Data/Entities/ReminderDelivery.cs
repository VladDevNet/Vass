namespace VoiceAssistant.API.Data.Entities;

public class ReminderDelivery
{
    public int Id { get; set; }
    public int ReminderId { get; set; }
    public Reminder Reminder { get; set; } = null!;
    public string DeviceId { get; set; } = null!;
    public string Status { get; set; } = ReminderDeliveryStatuses.Pending;
    public string? LocalNotificationId { get; set; }
    public string? Error { get; set; }
    public DateTime? ScheduledAtUtc { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public static class ReminderDeliveryStatuses
{
    public const string Pending = "pending";
    public const string Scheduled = "scheduled";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}
