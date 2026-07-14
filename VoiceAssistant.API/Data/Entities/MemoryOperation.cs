namespace VoiceAssistant.API.Data.Entities;

// Idempotency and clear-confirmation state deliberately hold no memory text.
public class MemoryOperation
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = null!;
    public User User { get; set; } = null!;
    public string Operation { get; set; } = null!;
    public string ArgumentsHash { get; set; } = null!;
    public string Status { get; set; } = "started";
    public string ResultCode { get; set; } = "pending";
    public Guid? MemoryItemId { get; set; }
    public string? ConfirmationTokenHash { get; set; }
    public DateTime? ConfirmationExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
