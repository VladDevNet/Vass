namespace PolishTutor.Api.Data.Entities;

public class Message
{
    public int Id { get; set; }
    public int ChatSessionId { get; set; }
    public ChatSession ChatSession { get; set; } = null!;
    public string Role { get; set; } = null!; // user, assistant
    public string Content { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? AudioFileName { get; set; }
}
