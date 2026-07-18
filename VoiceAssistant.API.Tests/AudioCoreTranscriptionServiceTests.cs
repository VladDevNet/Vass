using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceAssistant.API.Services;

namespace VoiceAssistant.API.Tests;

public class AudioCoreTranscriptionServiceTests
{
    [Fact]
    public async Task TranscribeAsync_SendsOriginalAudioAndReadsNativeFunctionArguments()
    {
        string? requestBody = null;
        var handler = new DelegateHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"candidates":[{"content":{"parts":[{"functionCall":{"name":"capture_user_utterance","args":{"transcript":"Поставь напоминание на завтра","preamble":"Сейчас проверю, как лучше это запланировать.","requiresTool":true}}}]}}]}
                    """, Encoding.UTF8, "application/json")
            };
        });
        var service = CreateService(handler);
        var audioPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.m4a");
        var audio = new byte[] { 0x00, 0x00, 0x00, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'M', (byte)'4', (byte)'A', (byte)' ' };
        await File.WriteAllBytesAsync(audioPath, audio);

        try
        {
            var result = await service.TranscribeAsync(audioPath, null, CancellationToken.None);

            Assert.True(result.ProviderAvailable);
            Assert.Equal("Поставь напоминание на завтра", result.Transcription);
            Assert.Equal("Сейчас проверю, как лучше это запланировать.", result.Preamble);
            Assert.True(result.RequiresTool);

            using var document = JsonDocument.Parse(requestBody!);
            var inlineData = document.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("inline_data");
            Assert.Equal("audio/aac", inlineData.GetProperty("mime_type").GetString());
            Assert.Equal(Convert.ToBase64String(audio), inlineData.GetProperty("data").GetString());
            Assert.Equal("capture_user_utterance", document.RootElement.GetProperty("tools")[0]
                .GetProperty("functionDeclarations")[0].GetProperty("name").GetString());
            var instruction = document.RootElement.GetProperty("systemInstruction").GetProperty("parts")[0]
                .GetProperty("text").GetString();
            Assert.Contains("локальную библиотеку", instruction);
        }
        finally
        {
            File.Delete(audioPath);
        }
    }

    [Fact]
    public void ParseResponse_WithoutExpectedFunctionCall_RequestsLegacyFallback()
    {
        var result = AudioCoreTranscriptionService.ParseResponse("""
            {"candidates":[{"content":{"parts":[{"text":"обычный текст вместо функции"}]}}]}
            """);

        Assert.False(result.ProviderAvailable);
        Assert.Equal("", result.Transcription);
    }

    [Fact]
    public void DetectAudioMimeType_RecognizesLegacyWebm()
    {
        var mimeType = AudioCoreTranscriptionService.DetectAudioMimeType([0x1A, 0x45, 0xDF, 0xA3]);

        Assert.Equal("audio/webm", mimeType);
    }

    [Fact]
    public void ParseResponse_OrdinaryConversation_AllowsConservativePlannerBypass()
    {
        var result = AudioCoreTranscriptionService.ParseResponse("""
            {"candidates":[{"content":{"parts":[{"functionCall":{"name":"capture_user_utterance","args":{"transcript":"Мне сегодня немного грустно","preamble":"Понимаю тебя, давай спокойно об этом поговорим.","requiresTool":false}}}]}}]}
            """);

        Assert.True(result.ProviderAvailable);
        Assert.False(result.RequiresTool);
        Assert.Equal("Понимаю тебя, давай спокойно об этом поговорим.", result.Preamble);
    }

    private static AudioCoreTranscriptionService CreateService(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Gemini:ApiKey"] = "test-key" })
            .Build();
        return new AudioCoreTranscriptionService(
            configuration,
            new TestHttpClientFactory(handler),
            NullLogger<AudioCoreTranscriptionService>.Instance);
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => callback(request);
    }
}
