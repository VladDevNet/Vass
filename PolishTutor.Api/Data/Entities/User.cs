using Microsoft.AspNetCore.Identity;

namespace PolishTutor.Api.Data.Entities;

public class User : IdentityUser
{
    public string NativeLang { get; set; } = "uk"; // uk, ru
    public string Level { get; set; } = "A1";      // A1, A2, B1, B2
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActiveAt { get; set; } = DateTime.UtcNow;

    public ICollection<ChatSession> ChatSessions { get; set; } = [];
    public ICollection<TestResult> TestResults { get; set; } = [];
    public ICollection<UserWord> UserWords { get; set; } = [];
    public ICollection<LearnerError> LearnerErrors { get; set; } = [];
    public LearningPlan? LearningPlan { get; set; }
    public TutorInstruction? TutorInstruction { get; set; }
}
