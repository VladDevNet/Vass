using Pgvector;

namespace VoiceAssistant.API.Data.Entities;

public class MemoryFact
{
    public int Id { get; set; }
    public string UserId { get; set; } = null!;
    public User User { get; set; } = null!;
    public string Fact { get; set; } = null!;
    public string ContentHash { get; set; } = null!;
    public Vector Embedding { get; set; } = null!;
    public string EmbeddingModel { get; set; } = null!;
    public int? SourceMessageId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastRecalledAt { get; set; }
    public int RecallCount { get; set; }
}
