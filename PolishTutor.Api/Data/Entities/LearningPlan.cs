namespace PolishTutor.Api.Data.Entities;

public class LearningPlan
{
    public int Id { get; set; }
    public string UserId { get; set; } = null!;
    public User User { get; set; } = null!;
    public string PlanJson { get; set; } = "{}"; // adaptive plan data
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
