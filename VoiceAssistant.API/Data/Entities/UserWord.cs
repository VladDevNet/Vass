namespace VoiceAssistant.API.Data.Entities;

public class UserWord
{
    public int Id { get; set; }
    public string UserId { get; set; } = null!;
    public User User { get; set; } = null!;
    public string Word { get; set; } = null!;
    public string Translation { get; set; } = null!;
    public string Status { get; set; } = "new"; // new, learning, known
    public string? GrammarInfo { get; set; } // JSON: declensions/conjugations
    public int ErrorCount { get; set; }
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
