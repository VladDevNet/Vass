namespace VoiceAssistant.API.Data.Entities;

public class MessageAttachment
{
    public int Id { get; set; }
    public int MessageId { get; set; }
    public Message Message { get; set; } = null!;
    public Guid VisualAssetId { get; set; }
    public VisualAsset VisualAsset { get; set; } = null!;
    public string Kind { get; set; } = "image";
}
