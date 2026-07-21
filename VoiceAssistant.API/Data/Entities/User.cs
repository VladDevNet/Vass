using Microsoft.AspNetCore.Identity;

namespace VoiceAssistant.API.Data.Entities;

public class User : IdentityUser
{
    public string NativeLang { get; set; } = "uk"; // uk, ru
    public string Level { get; set; } = "A1";      // A1, A2, B1, B2
    public bool IsApproved { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActiveAt { get; set; } = DateTime.UtcNow;
    // Limits the one optional agent-planner opportunity that can introduce a
    // new capability. It does not reveal which feature was considered.
    public DateTime? LastCapabilityDiscoveryConsideredAt { get; set; }

    public ICollection<ChatSession> ChatSessions { get; set; } = [];
    public ICollection<VisualAsset> VisualAssets { get; set; } = [];
    public ICollection<MemoryItem> MemoryItems { get; set; } = [];
}
