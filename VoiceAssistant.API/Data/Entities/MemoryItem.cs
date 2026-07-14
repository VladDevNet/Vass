using Pgvector;

namespace VoiceAssistant.API.Data.Entities;

// Canonical, server-owned memory record. Legacy MemoryFact remains readable
// during the transition, but new writes must land here first.
public class MemoryItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = null!;
    public User User { get; set; } = null!;
    public string Kind { get; set; } = "semantic_fact";
    public string Text { get; set; } = null!;
    public string ContentHash { get; set; } = null!;
    public string Status { get; set; } = "active";
    public int Revision { get; set; } = 1;
    public Guid? SupersedesMemoryItemId { get; set; }
    public int? LegacyMemoryFactId { get; set; }
    public int? SourceMessageId { get; set; }
    public DateTime? TombstonedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public Vector? Embedding { get; set; }
    public string? EmbeddingModel { get; set; }
    public string EmbeddingState { get; set; } = "pending";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastRecalledAt { get; set; }
    public int RecallCount { get; set; }
}
