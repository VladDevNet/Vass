namespace VoiceAssistant.API.Data.Entities;

public class TestResult
{
    public int Id { get; set; }
    public string UserId { get; set; } = null!;
    public User User { get; set; } = null!;
    public string TestType { get; set; } = "level"; // level
    public string Level { get; set; } = null!;       // determined level
    public int Score { get; set; }
    public int MaxScore { get; set; }
    public string AnswersJson { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
