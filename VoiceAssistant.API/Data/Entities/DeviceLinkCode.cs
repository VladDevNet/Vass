namespace VoiceAssistant.API.Data.Entities;

// A short-lived, single-use code that lets an already-logged-in session
// (e.g. a family member's phone) log a new device into the SAME account
// without typing an email/password there — the elderly-friendly login path
// from AUDIT-LEGACY.md / docs/react-native/BACKLOG.md Phase 0.
public class DeviceLinkCode
{
    public int Id { get; set; }
    public string UserId { get; set; } = null!;
    public User User { get; set; } = null!;

    public string Code { get; set; } = null!;
    public bool Used { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
}
