namespace VoiceAssistant.API.Data.Entities;

public class Reminder
{
    public int Id { get; set; }
    public string UserId { get; set; } = null!;
    public User User { get; set; } = null!;
    public string Text { get; set; } = null!;
    public DateTime DueAtUtc { get; set; }
    public string TimeZoneId { get; set; } = null!;
    public string? RecurrenceRule { get; set; }
    public Guid? OperationId { get; set; }
    public string Status { get; set; } = ReminderStatuses.Active;
    public string CreatedByDeviceId { get; set; } = null!;
    public int? SourceMessageId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<ReminderDelivery> Deliveries { get; set; } = [];
}

public static class ReminderStatuses
{
    public const string Active = "active";
    public const string Cancelled = "cancelled";
}
