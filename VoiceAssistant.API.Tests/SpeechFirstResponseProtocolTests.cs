using VoiceAssistant.API.Services;

namespace VoiceAssistant.API.Tests;

public class SpeechFirstResponseProtocolTests
{
    [Fact]
    public void Parser_StreamsSpeechBeforeDisplayAcrossMarkerBoundaries()
    {
        var parser = new SpeechFirstResponseParser();
        var emitted = new List<SpeechFirstResponseChunk>();

        emitted.AddRange(parser.Append("[[VASS_SP"));
        emitted.AddRange(parser.Append("EECH]]Открою Джемини Лайв. [[VASS_TE"));
        emitted.AddRange(parser.Append("XT]]Открою Gemini Live."));
        emitted.AddRange(parser.Finish());

        Assert.Equal(
        [
            new SpeechFirstResponseChunk(SpeechFirstResponsePart.Speech, "Открою Джемини Лайв. "),
            new SpeechFirstResponseChunk(SpeechFirstResponsePart.Text, "Открою Gemini Live.")
        ], emitted);
        Assert.True(parser.HasSpeech);
        Assert.True(parser.HasText);
    }

    [Fact]
    public void Parser_FallsBackToLegacyTextWithoutDelayingIt()
    {
        var parser = new SpeechFirstResponseParser();

        var emitted = parser.Append("Открою видео в YouTube.");

        Assert.Equal(
        [new SpeechFirstResponseChunk(SpeechFirstResponsePart.Text, "Открою видео в YouTube.")],
        emitted);
        Assert.False(parser.HasSpeech);
        Assert.True(parser.HasText);
        Assert.Empty(parser.Finish());
    }

    [Fact]
    public void Parser_UsesSpeechAsVisibleFallbackWhenDisplayMarkerIsMissing()
    {
        var parser = new SpeechFirstResponseParser();
        var emitted = new List<SpeechFirstResponseChunk>();

        emitted.AddRange(parser.Append("[[VASS_SPEECH]]Открою Джемини Лайв."));
        emitted.AddRange(parser.Finish());

        Assert.Equal(
        [
            new SpeechFirstResponseChunk(SpeechFirstResponsePart.Speech, "Открою Джемини Лайв."),
            new SpeechFirstResponseChunk(SpeechFirstResponsePart.Text, "Открою Джемини Лайв.")
        ], emitted);
    }

    [Fact]
    public void AddInstructions_RequiresExactInternalMarkers()
    {
        var prompt = SpeechFirstResponseParser.AddInstructions("base");

        Assert.Contains(SpeechFirstResponseParser.SpeechStartMarker, prompt);
        Assert.Contains(SpeechFirstResponseParser.TextStartMarker, prompt);
        Assert.StartsWith("base", prompt, StringComparison.Ordinal);
    }
}
