using System.Text.Json;
using VoiceAssistant.API.Services;

namespace VoiceAssistant.API.Tests;

public class AssistantAgentTurnServiceTests
{
    [Fact]
    public void TryGetDirectWebSearchFinalText_OnlySuccessfulGroundedSearch_ReturnsItsSummary()
    {
        var call = new AssistantToolCall("web_search", JsonSerializer.SerializeToElement(new { query = "latest news" }), "call-1");
        var execution = new AssistantToolExecution("web_search", "grounded", "Проверенная краткая выжимка.");

        var direct = AssistantAgentTurnService.TryGetDirectWebSearchFinalText(
            [execution],
            [call],
            [execution],
            out var finalText);

        Assert.True(direct);
        Assert.Equal("Проверенная краткая выжимка.", finalText);
    }

    [Fact]
    public void TryGetDirectWebSearchFinalText_AdditionalWork_DoesNotBypassPlanner()
    {
        var call = new AssistantToolCall("web_search", JsonSerializer.SerializeToElement(new { query = "latest news" }), "call-1");
        var search = new AssistantToolExecution("web_search", "grounded", "Проверенная выжимка.");
        var extra = new AssistantToolExecution("memory_status", "ok", "Память доступна.");

        var direct = AssistantAgentTurnService.TryGetDirectWebSearchFinalText(
            [search, extra],
            [call],
            [search],
            out var finalText);

        Assert.False(direct);
        Assert.Null(finalText);
    }

    [Fact]
    public void BuildInitialContents_MultimodalMessage_PreservesInlineImageAlongsideText()
    {
        var image = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };

        var contents = AssistantAgentTurnService.BuildInitialContents(
        [
            new GeminiMessage("user",
            [
                new GeminiPart(Text: "Что на этом изображении?"),
                new GeminiPart(MimeType: "image/jpeg", Data: image),
            ]),
        ]);

        var content = Assert.Single(contents);
        var parts = content.GetProperty("parts");
        Assert.Equal("Что на этом изображении?", parts[0].GetProperty("text").GetString());
        var inlineData = parts[1].GetProperty("inline_data");
        Assert.Equal("image/jpeg", inlineData.GetProperty("mime_type").GetString());
        Assert.Equal(Convert.ToBase64String(image), inlineData.GetProperty("data").GetString());
    }
}
