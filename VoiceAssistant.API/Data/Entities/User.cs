using Microsoft.AspNetCore.Identity;

namespace VoiceAssistant.API.Data.Entities;

public class User : IdentityUser
{
    public string NativeLang { get; set; } = "uk"; // uk, ru
    public string Level { get; set; } = "A1";      // A1, A2, B1, B2
    public string? DisplayName { get; set; }       // what the assistant calls the user; set during onboarding
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActiveAt { get; set; } = DateTime.UtcNow;

    public ICollection<ChatSession> ChatSessions { get; set; } = [];
}
