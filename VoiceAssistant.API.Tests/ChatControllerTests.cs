using System.Text.Json;
using VoiceAssistant.API.Controllers;
using VoiceAssistant.API.Data.Entities;
using VoiceAssistant.API.Services;
using Xunit;

namespace VoiceAssistant.API.Tests;

public class ChatControllerTests
{
    private const string AudioRoot = "/app/audio";

    [Fact]
    public void LibraryArtifactAction_JsonUsesTheMobileCamelCaseContract()
    {
        var action = new LibraryArtifactAction(
            "cfb4dfe9-0fae-45b9-a1fa-5a4701cb854b",
            "Ужины на неделю",
            "recipes",
            "<article><p>Тест</p></article>",
            "Кулинария",
            "Пять рецептов",
            ["https://example.com/source"],
            "Добавлены быстрые ужины");

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { libraryArtifact = action }));
        var artifact = document.RootElement.GetProperty("libraryArtifact");

        Assert.Equal("Ужины на неделю", artifact.GetProperty("title").GetString());
        Assert.Equal("recipes", artifact.GetProperty("kind").GetString());
        Assert.Equal("<article><p>Тест</p></article>", artifact.GetProperty("html").GetString());
        Assert.Equal("Кулинария", artifact.GetProperty("sectionTitle").GetString());
        Assert.False(artifact.TryGetProperty("Title", out _));
        Assert.False(artifact.TryGetProperty("Html", out _));
        Assert.False(artifact.TryGetProperty("SectionTitle", out _));
    }

    [Fact]
    public void TryResolveSafeAudioPath_ValidGuidWebm_ReturnsTrueWithCombinedPath()
    {
        var ok = ChatController.TryResolveSafeAudioPath(AudioRoot, "3fa85f64-5717-4562-b3fc-2c963f66afa6.webm", out var path);

        Assert.True(ok);
        Assert.Equal(Path.GetFullPath(Path.Combine(AudioRoot, "3fa85f64-5717-4562-b3fc-2c963f66afa6.webm")), path);
    }

    [Fact]
    public void TryResolveSafeAudioPath_UppercaseGuid_StillMatches()
    {
        var ok = ChatController.TryResolveSafeAudioPath(AudioRoot, "3FA85F64-5717-4562-B3FC-2C963F66AFA6.webm", out _);

        Assert.True(ok);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\..\\windows\\win.ini")]
    [InlineData("3fa85f64-5717-4562-b3fc-2c963f66afa6.webm/../../../etc/passwd")]
    public void TryResolveSafeAudioPath_PathTraversalAttempt_ReturnsFalse(string maliciousInput)
    {
        var ok = ChatController.TryResolveSafeAudioPath(AudioRoot, maliciousInput, out var path);

        Assert.False(ok);
        Assert.Equal("", path);
    }

    [Theory]
    [InlineData("not-a-guid.webm")]
    [InlineData("3fa85f64-5717-4562-b3fc-2c963f66afa6.mp3")]
    [InlineData("3fa85f64-5717-4562-b3fc-2c963f66afa6")]
    [InlineData("")]
    [InlineData("3fa85f64-5717-4562-b3fc-2c963f66afa6.webm.exe")]
    [InlineData("3fa85f64-5717-4562-b3fc-2c963f66afa6.webm\n")]
    public void TryResolveSafeAudioPath_WrongShape_ReturnsFalse(string input)
    {
        var ok = ChatController.TryResolveSafeAudioPath(AudioRoot, input, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryResolveSafeAudioPath_AbsolutePathAsFileName_ReturnsFalse()
    {
        var ok = ChatController.TryResolveSafeAudioPath(AudioRoot, "/etc/passwd", out _);

        Assert.False(ok);
    }

    [Fact]
    public void ResolveAudioPath_ConfiguredPathIsRooted_UsedAsIs()
    {
        var contentRoot = Path.GetFullPath("/app");
        var configured = Path.Combine(contentRoot, "custom-audio");

        var resolved = ChatController.ResolveAudioPath(configured, contentRoot);

        Assert.Equal(Path.GetFullPath(configured), resolved);
    }

    [Fact]
    public void ResolveAudioPath_ConfiguredPathIsRelative_CombinedWithContentRoot()
    {
        var contentRoot = Path.GetFullPath("/app");

        var resolved = ChatController.ResolveAudioPath("relative-audio", contentRoot);

        Assert.Equal(Path.GetFullPath(Path.Combine(contentRoot, "relative-audio")), resolved);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveAudioPath_NoConfiguredPath_DefaultsToAudioUnderContentRoot(string? configured)
    {
        var contentRoot = Path.GetFullPath("/app");

        var resolved = ChatController.ResolveAudioPath(configured, contentRoot);

        Assert.Equal(Path.GetFullPath(Path.Combine(contentRoot, "audio")), resolved);
    }

    [Fact]
    public void TryDetectImageMimeType_Jpeg_ReturnsTrue()
    {
        byte[] content = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46];

        var ok = ChatController.TryDetectImageMimeType(content, out var mimeType);

        Assert.True(ok);
        Assert.Equal("image/jpeg", mimeType);
    }

    [Fact]
    public void TryDetectImageMimeType_Png_ReturnsTrue()
    {
        byte[] content = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00];

        var ok = ChatController.TryDetectImageMimeType(content, out var mimeType);

        Assert.True(ok);
        Assert.Equal("image/png", mimeType);
    }

    [Fact]
    public void TryDetectImageMimeType_Gif_ReturnsTrue()
    {
        byte[] content = "GIF89a"u8.ToArray();

        var ok = ChatController.TryDetectImageMimeType(content, out var mimeType);

        Assert.True(ok);
        Assert.Equal("image/gif", mimeType);
    }

    [Fact]
    public void TryDetectImageMimeType_WebP_ReturnsTrue()
    {
        // RIFF <4-byte size, contents irrelevant here> WEBP
        byte[] content = [.."RIFF"u8.ToArray(), 0x00, 0x00, 0x00, 0x00, .."WEBP"u8.ToArray()];

        var ok = ChatController.TryDetectImageMimeType(content, out var mimeType);

        Assert.True(ok);
        Assert.Equal("image/webp", mimeType);
    }

    [Fact]
    public void TryDetectImageMimeType_RiffButNotWebp_ReturnsFalse()
    {
        // A real RIFF container (e.g. a WAV file) that isn't WEBP must not match.
        byte[] content = [.."RIFF"u8.ToArray(), 0x00, 0x00, 0x00, 0x00, .."WAVE"u8.ToArray()];

        var ok = ChatController.TryDetectImageMimeType(content, out _);

        Assert.False(ok);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x01, 0x02 })]
    public void TryDetectImageMimeType_EmptyOrTooShort_ReturnsFalse(byte[] content)
    {
        var ok = ChatController.TryDetectImageMimeType(content, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryDetectImageMimeType_SpoofedNonImageContent_ReturnsFalse()
    {
        // e.g. a PDF or script renamed to "photo.jpg" with a forged
        // Content-Type: image/jpeg -- the bytes themselves must fail.
        byte[] content = "%PDF-1.4 some content that is not an image at all"u8.ToArray();

        var ok = ChatController.TryDetectImageMimeType(content, out var mimeType);

        Assert.False(ok);
        Assert.Equal("", mimeType);
    }

    [Fact]
    public void ResolvePromptUserName_SpeakerKnown_DiffersFromDisplayName_SpeakerWins()
    {
        // The actual collision this closes: a family member sharing the
        // device (device-link), recognized by speaker-id under a name that
        // isn't the account's own DisplayName -- must resolve to exactly
        // one name, not both.
        var resolved = ChatController.ResolvePromptUserName("Антон", "Владислав");

        Assert.Equal("Антон", resolved);
    }

    [Fact]
    public void ResolvePromptUserName_NoSpeakerMatch_FallsBackToDisplayName()
    {
        // The only case that ever happens today: Features:SpeakerIdentificationEnabled
        // is off, so speakerKnownName is always null.
        var resolved = ChatController.ResolvePromptUserName(null, "Владислав");

        Assert.Equal("Владислав", resolved);
    }

    [Fact]
    public void ResolvePromptUserName_NeitherSet_ReturnsNull()
    {
        var resolved = ChatController.ResolvePromptUserName(null, null);

        Assert.Null(resolved);
    }

    [Theory]
    [InlineData("reminder_create(reason: \\\"Позвонить врачу\\\")")]
    [InlineData("search{queries:[\\\"Europe/Warsaw\\\"]}")]
    [InlineData("memory_remember({ text: \\\"Влад любит кофе\\\" })")]
    public void LooksLikeInternalProtocolReply_ToolSyntax_ReturnsTrue(string text)
    {
        Assert.True(ChatController.LooksLikeInternalProtocolReply(text));
    }

    [Theory]
    [InlineData("Напоминание установлено на телефоне.")]
    [InlineData("Я посмотрю, что можно сделать.")]
    public void LooksLikeInternalProtocolReply_NaturalReply_ReturnsFalse(string text)
    {
        Assert.False(ChatController.LooksLikeInternalProtocolReply(text));
    }

    [Fact]
    public void BuildReminderReceiptReply_Scheduled_UsesVerifiedHumanReadableConfirmation()
    {
        var reminder = new ReminderDraft(1, "Проверить обновление машины", DateTime.UtcNow, "Europe/Warsaw", "device-1");

        var reply = ChatController.BuildReminderReceiptReply(reminder, ReminderDeliveryStatuses.Scheduled);

        Assert.Contains("Проверить обновление машины", reply);
        Assert.Contains("установлено на телефоне", reply);
        Assert.DoesNotContain("reminder_create", reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryGetWebSearchQuery_FailedSearchWithQuery_ReturnsOriginalQuery()
    {
        using var document = JsonDocument.Parse("""{"query":"новости робототехники и ИИ","status":"unavailable"}""");
        var execution = new AssistantToolExecution("web_search", "unavailable", "Поиск временно недоступен.", Data: document.RootElement.Clone());

        var found = ChatController.TryGetWebSearchQuery(execution, out var query);

        Assert.True(found);
        Assert.Equal("новости робототехники и ИИ", query);
    }

    [Fact]
    public void TryGetWebSearchQuery_MissingOrNonSearchData_ReturnsFalse()
    {
        using var document = JsonDocument.Parse("""{"query":"новости"}""");
        var execution = new AssistantToolExecution("memory_search", "ok", "Готово.", Data: document.RootElement.Clone());

        var found = ChatController.TryGetWebSearchQuery(execution, out _);

        Assert.False(found);
    }

}
