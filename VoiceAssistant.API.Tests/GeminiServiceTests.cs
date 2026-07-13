using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceAssistant.API.Services;
using Xunit;

namespace VoiceAssistant.API.Tests;

public class GeminiServiceTests
{
    [Fact]
    public async Task StreamResponseAsync_MultimodalMessage_SendsInlineImageAlongsideText()
    {
        string? requestBody = null;
        var handler = new DelegateHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"ok\"}]}}]}\n\n", Encoding.UTF8, "text/event-stream")
            };
        });
        var service = CreateService(handler);
        var image = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };
        var messages = new List<GeminiMessage>
        {
            new("user", new GeminiPart[]
            {
                new(Text: "Что на этом изображении?"),
                new(MimeType: "image/jpeg", Data: image),
            })
        };

        await foreach (var _ in service.StreamResponseAsync("system", messages, enableGrounding: false)) { }

        using var document = JsonDocument.Parse(requestBody!);
        var parts = document.RootElement.GetProperty("contents")[0].GetProperty("parts");
        Assert.Equal("Что на этом изображении?", parts[0].GetProperty("text").GetString());
        var inlineData = parts[1].GetProperty("inline_data");
        Assert.Equal("image/jpeg", inlineData.GetProperty("mime_type").GetString());
        Assert.Equal(Convert.ToBase64String(image), inlineData.GetProperty("data").GetString());
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_ParsesExpectedDimensionsAndRequestContract()
    {
        string? requestBody = null;
        Uri? requestUri = null;
        var values = Enumerable.Repeat(0.125f, GeminiService.EmbeddingDimensions).ToArray();
        var handler = new DelegateHandler(async request =>
        {
            requestUri = request.RequestUri;
            requestBody = await request.Content!.ReadAsStringAsync();
            var json = JsonSerializer.Serialize(new { embedding = new { values } });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });
        var service = CreateService(handler);

        var result = await service.GenerateEmbeddingAsync("task: search result | query: чай");

        Assert.Equal(GeminiService.EmbeddingDimensions, result.Length);
        Assert.Contains($"models/{GeminiService.EmbeddingModel}:embedContent", requestUri!.AbsoluteUri);
        Assert.Contains($"\"output_dimensionality\":{GeminiService.EmbeddingDimensions}", requestBody);
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_WrongDimensions_ThrowsTypedError()
    {
        var handler = new DelegateHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"embedding\":{\"values\":[0.1,0.2]}}", Encoding.UTF8, "application/json")
        }));
        var service = CreateService(handler);

        var error = await Assert.ThrowsAsync<GeminiApiException>(() => service.GenerateEmbeddingAsync("query"));

        Assert.False(error.IsRetryable);
    }

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

    private static GeminiService CreateService(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Gemini:ApiKey"] = "test-key" })
            .Build();
        return new GeminiService(configuration, NullLogger<GeminiService>.Instance, new TestHttpClientFactory(handler));
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
