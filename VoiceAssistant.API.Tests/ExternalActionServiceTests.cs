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
}
