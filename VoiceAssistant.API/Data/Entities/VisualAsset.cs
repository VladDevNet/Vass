namespace VoiceAssistant.API.Data.Entities;

public class VisualAsset
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = null!;
    public User User { get; set; } = null!;
    public string StorageFileName { get; set; } = null!;
    public string MimeType { get; set; } = null!;
    public long SizeBytes { get; set; }
    public string? OriginalFileName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<MessageAttachment> MessageAttachments { get; set; } = [];
    public ICollection<MemoryItem> MemoryItems { get; set; } = [];
}
