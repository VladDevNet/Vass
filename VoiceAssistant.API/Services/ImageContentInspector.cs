namespace VoiceAssistant.API.Services;

public static class ImageContentInspector
{
    // Gemini accepts inline PDFs up to 50 MiB. Keeping the same ceiling for
    // every shared attachment makes the client contract predictable while
    // still allowing normal documents, screenshots, and photos.
    public const long MaxAttachmentSize = 50 * 1024 * 1024;

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

    public static bool IsImageMimeType(string mimeType) =>
        mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    public static bool TryNormalizeAttachmentMimeType(string? rawMimeType, out string mimeType)
    {
        var candidate = rawMimeType?.Split(';', 2)[0].Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(candidate) &&
            System.Text.RegularExpressions.Regex.IsMatch(candidate, @"\A[a-z0-9!#$&^_.+-]+/[a-z0-9!#$&^_.+-]+\z"))
        {
            mimeType = candidate;
            return true;
        }

        mimeType = "application/octet-stream";
        return false;
    }

    public static string ExtensionForMimeType(string mimeType) => mimeType switch
    {
        "image/jpeg" => "jpg",
        "image/png" => "png",
        "image/webp" => "webp",
        _ => "bin",
    };
}
