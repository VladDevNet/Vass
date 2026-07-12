using VoiceAssistant.API.Services;

namespace VoiceAssistant.API.Tests;

public class ExternalActionServiceTests
{
    [Fact]
    public void Parse_Search_NormalizesQuery()
    {
        var action = ExternalActionService.Parse(
            "```json\n{\"type\":\"youtube_search\",\"query\":\"  песни   Высоцкого  \",\"videoId\":null}\n```");

        Assert.NotNull(action);
        Assert.Equal(ExternalActionTypes.YouTubeSearch, action.Type);
        Assert.Equal("песни Высоцкого", action.Query);
        Assert.Null(action.VideoId);
    }

    [Fact]
    public void Parse_Watch_AcceptsOnlyYouTubeVideoIdShape()
    {
        var action = ExternalActionService.Parse(
            "{\"type\":\"youtube_watch\",\"query\":null,\"videoId\":\"dQw4w9WgXcQ\"}");

        Assert.NotNull(action);
        Assert.Equal(ExternalActionTypes.YouTubeWatch, action.Type);
        Assert.Equal("dQw4w9WgXcQ", action.VideoId);
    }

    [Theory]
    [InlineData("{\"type\":\"youtube_watch\",\"query\":null,\"videoId\":\"https://evil.example\"}")]
    [InlineData("{\"type\":\"youtube_search\",\"query\":\"\",\"videoId\":null}")]
    [InlineData("{\"type\":\"open_url\",\"query\":\"https://evil.example\",\"videoId\":null}")]
    [InlineData("not-json")]
    public void Parse_UnsafeOrIncompleteAction_ReturnsNull(string raw)
    {
        Assert.Null(ExternalActionService.Parse(raw));
    }

    [Fact]
    public void Parse_OpenVass_DropsUntrustedParameters()
    {
        var action = ExternalActionService.Parse(
            "{\"type\":\"open_vass\",\"query\":\"https://evil.example\",\"videoId\":\"dQw4w9WgXcQ\"}");

        Assert.Equal(new ExternalActionCommand(ExternalActionTypes.OpenVass), action);
    }

    [Fact]
    public void Parse_PlayWithoutValidId_FallsBackToSafeSearch()
    {
        var action = ExternalActionService.Parse(
            "{\"type\":\"youtube_play\",\"query\":\"лекции по истории\",\"videoId\":\"invented\"}");

        Assert.Equal(
            new ExternalActionCommand(ExternalActionTypes.YouTubeSearch, Query: "лекции по истории"),
            action);
    }

    [Fact]
    public void ResolveFromContext_LaunchFollowUp_UsesLatestProposedVideo()
    {
        var action = ExternalActionService.ResolveFromContext(
            "Запускай. Посмотрим.",
            [
                new GeminiMessage("assistant", "Первое: https://www.youtube.com/watch?v=bWGXT5wjkd4"),
                new GeminiMessage("user", "Хорошо")
            ]);

        Assert.Equal(
            new ExternalActionCommand(ExternalActionTypes.YouTubeWatch, VideoId: "bWGXT5wjkd4"),
            action);
    }

    [Fact]
    public void ResolveFromContext_SecondSelection_UsesSecondVideo()
    {
        var action = ExternalActionService.ResolveFromContext(
            "Давай второе",
            [new GeminiMessage(
                "assistant",
                "1. https://youtu.be/bWGXT5wjkd4 2. https://www.youtube.com/watch?v=dQw4w9WgXcQ")]);

        Assert.Equal("dQw4w9WgXcQ", action?.VideoId);
    }

    [Fact]
    public void ResolveFromContext_OrdinaryReply_DoesNotOpenPriorLink()
    {
        var action = ExternalActionService.ResolveFromContext(
            "Расскажи подробнее",
            [new GeminiMessage("assistant", "https://www.youtube.com/watch?v=bWGXT5wjkd4")]);

        Assert.Null(action);
    }
}
