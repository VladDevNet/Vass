using VoiceAssistant.API.Controllers;
using Xunit;

namespace VoiceAssistant.API.Tests;

public class ChatControllerTests
{
    private const string AudioRoot = "/app/audio";

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
}
