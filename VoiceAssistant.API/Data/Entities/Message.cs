namespace VoiceAssistant.API.Data.Entities;

public class Message
{
    public int Id { get; set; }
    public int ChatSessionId { get; set; }
    public ChatSession ChatSession { get; set; } = null!;
    public string Role { get; set; } = null!; // user, assistant
    public string Content { get; set; } = null!;
    // JSON capability ids/states shown to the model for this user turn. It is
    // deliberately content-free, allowing incident review without copying a turn.
    public string? CapabilitySnapshotJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? AudioFileName { get; set; }
    public ICollection<MessageAttachment> Attachments { get; set; } = [];
}
