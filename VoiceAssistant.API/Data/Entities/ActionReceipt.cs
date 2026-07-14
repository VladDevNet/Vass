namespace VoiceAssistant.API.Data.Entities;

// Records execution state without storing URLs, queries, or user content.
// `handler_dispatched` deliberately means only that Android/iOS handed the
// request to a handler; it does not claim playback or destination success.
public class ActionReceipt
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = null!;
    public User User { get; set; } = null!;
    public int SourceMessageId { get; set; }
    public string ActionType { get; set; } = null!;
    public string Taxonomy { get; set; } = null!;
    public string Status { get; set; } = "proposed";
    public string? ResultCode { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
