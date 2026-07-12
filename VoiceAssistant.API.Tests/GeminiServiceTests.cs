using VoiceAssistant.API.Services;
using Xunit;

namespace VoiceAssistant.API.Tests;

public class GeminiServiceTests
{
    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(599)]
    public void IsRetryableStatusCode_TransientCodes_ReturnsTrue(int statusCode)
    {
        Assert.True(GeminiService.IsRetryableStatusCode(statusCode));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(499)]
    public void IsRetryableStatusCode_ClientErrorCodes_ReturnsFalse(int statusCode)
    {
        Assert.False(GeminiService.IsRetryableStatusCode(statusCode));
    }

    [Fact]
    public void GeminiApiException_ExposesMessageAndRetryable()
    {
        var ex = new GeminiApiException("Ошибка API Gemini: 503", isRetryable: true);

        Assert.Equal("Ошибка API Gemini: 503", ex.Message);
        Assert.True(ex.IsRetryable);
    }
}
