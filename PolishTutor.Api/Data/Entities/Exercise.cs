namespace PolishTutor.Api.Data.Entities;

public class Exercise
{
    public int Id { get; set; }
    public int LessonId { get; set; }
    public Lesson Lesson { get; set; } = null!;
    public string Type { get; set; } = null!; // fill_gap, translate, choice
    public string DataJson { get; set; } = null!; // JSON with exercise data
    public int SortOrder { get; set; }
}
