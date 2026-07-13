namespace VoiceAssistant.API.Services;

public static class ImageContentInspector
{
    public const long MaxVisualSize = 10 * 1024 * 1024;

    private static readonly (byte[] Signature, string MimeType)[] Signatures =
    [
        (new byte[] { 0xFF, 0xD8, 0xFF }, "image/jpeg"),
        (new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "image/png"),
        (new byte[] { 0x47, 0x49, 0x46, 0x38 }, "image/gif"),
    ];

    public static bool TryDetectMimeType(ReadOnlySpan<byte> content, out string mimeType)
    {
        foreach (var (signature, mime) in Signatures)
        {
            if (content.Length >= signature.Length && content[..signature.Length].SequenceEqual(signature))
            {
                mimeType = mime;
                return true;
            }
        }

        if (content.Length >= 12 &&
            content[..4].SequenceEqual("RIFF"u8) &&
            content.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            mimeType = "image/webp";
            return true;
        }

        mimeType = "";
        return false;
    }

    public static bool IsVisualCaptureMimeType(string mimeType) =>
        mimeType is "image/jpeg" or "image/png" or "image/webp";

    public static string ExtensionForMimeType(string mimeType) => mimeType switch
    {
        "image/jpeg" => "jpg",
        "image/png" => "png",
        "image/webp" => "webp",
        _ => throw new ArgumentOutOfRangeException(nameof(mimeType), mimeType, "Unsupported visual asset MIME type"),
    };
}
