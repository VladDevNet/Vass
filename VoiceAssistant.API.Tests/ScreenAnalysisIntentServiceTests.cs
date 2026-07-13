using VoiceAssistant.API.Services;

namespace VoiceAssistant.API.Tests;

public class ScreenAnalysisIntentServiceTests
{
    [Theory]
    [InlineData("Объясни, что сейчас на экране")]
    [InlineData("Что здесь нажать в интерфейсе?")]
    [InlineData("Read the screen for me")]
    public void IsCandidate_ExplicitScreenRequest_ReturnsTrue(string text) => Assert.True(ScreenAnalysisIntentService.IsCandidate(text));

    [Theory]
    [InlineData("Расскажи про новый экран в кино")]
    [InlineData("Что мне сегодня сделать?")]
    public void IsCandidate_OrdinaryConversation_ReturnsFalse(string text) => Assert.False(ScreenAnalysisIntentService.IsCandidate(text));

    [Theory]
    [InlineData("{\"type\":\"screen_analyze\"}", true)]
    [InlineData("```json\n{\"type\":\"screen_analyze\"}\n```", true)]
    [InlineData("{\"type\":\"chat\"}", false)]
    [InlineData("not json", false)]
    public void IsScreenAnalysisJson_OnlyExplicitActionReturnsTrue(string raw, bool expected) =>
        Assert.Equal(expected, ScreenAnalysisIntentService.IsScreenAnalysisJson(raw));
}
