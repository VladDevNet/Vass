namespace VoiceAssistant.API.Data.Entities;

public class ChatSession
{
    public int Id { get; set; }
    public string UserId { get; set; } = null!;
    public User User { get; set; } = null!;
    public string Mode { get; set; } = "dialog"; // dialog, lesson, situation
    public string? Title { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Message> Messages { get; set; } = [];
}
