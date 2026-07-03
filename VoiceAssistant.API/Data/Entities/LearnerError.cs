namespace VoiceAssistant.API.Data.Entities;

public class LearnerError
{
    public int Id { get; set; }
    public string UserId { get; set; } = null!;
    public User User { get; set; } = null!;
    public int ChatSessionId { get; set; }
    public ChatSession ChatSession { get; set; } = null!;
    public string ErrorType { get; set; } = null!; // grammar, vocabulary, spelling
    public string Original { get; set; } = null!;
    public string Corrected { get; set; } = null!;
    public string? GrammarTopic { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
