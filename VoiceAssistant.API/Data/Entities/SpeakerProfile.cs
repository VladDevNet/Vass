namespace VoiceAssistant.API.Data.Entities;

public class SpeakerProfile
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string EmbeddingJson { get; set; } = null!; // JSON array of floats (192-dim, ECAPA-TDNN)
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
