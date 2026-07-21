namespace VoiceAssistant.API.Data.Entities;

// Content-free per-user progress for optional capability hints. It records
// only stable feature IDs and timestamps/counters, never a prompt, message,
// attachment name, or model response.
public class CapabilityDiscoveryProgress
{
    public long Id { get; set; }
    public string UserId { get; set; } = null!;
    public User User { get; set; } = null!;
    public string CapabilityId { get; set; } = null!;
    public int UsageCount { get; set; }
    public DateTime? FirstUsedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public int SuggestionCount { get; set; }
    public DateTime? FirstSuggestedAt { get; set; }
    public DateTime? LastSuggestedAt { get; set; }
    // A pending suggestion only needs the following user turn to recognize
    // an explicit refusal; after that it becomes an ordinary cooldown item.
    public DateTime? PendingResponseSeenAt { get; set; }
    public DateTime? DeclinedAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
