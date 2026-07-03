namespace VoiceAssistant.API.Data.Entities;

public class Lesson
{
    public int Id { get; set; }
    public string Title { get; set; } = null!;
    public string Level { get; set; } = "A1";
    public string Content { get; set; } = null!; // markdown
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Exercise> Exercises { get; set; } = [];
}
