using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceAssistant.API.Services;
using Xunit;

namespace VoiceAssistant.API.Tests;

public class GroundedWebSearchServiceTests
{
    [Fact]
    public async Task SearchAsync_ReturnsOnlyVerifiedGroundedResult()
    {
        string? requestBody = null;
        var handler = new DelegateHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            const string payload = """
                {
                  "candidates": [{
                    "content": { "parts": [{ "text": "Краткая подтвержденная выжимка." }] },
                    "groundingMetadata": {
                      "webSearchQueries": ["свежие новости"],
                      "groundingChunks": [
                        { "web": { "title": "Reuters", "uri": "https://www.reuters.com/example" } },
                        { "web": { "title": "Повтор", "uri": "https://www.reuters.com/example" } }
                      ]
                    }
                  }]
                }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        });
        var service = CreateService(handler);

        var result = await service.SearchAsync("  свежие   новости  ", null, CancellationToken.None);

        Assert.Equal("grounded", result.Status);
        Assert.Equal("Краткая подтвержденная выжимка.", result.Summary);
        Assert.Equal(1, result.QueryCount);
        var source = Assert.Single(result.Sources);
        Assert.Equal("Reuters", source.Title);
        Assert.Equal("https://www.reuters.com/example", source.Url);
        Assert.Contains("google_search", requestBody!);
        var capturedRequest = Assert.IsType<string>(requestBody);
        using var requestDocument = JsonDocument.Parse(capturedRequest);
        var queryText = requestDocument.RootElement
            .GetProperty("contents")[0]
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();
        Assert.Contains("свежие новости", queryText);
    }

    [Fact]
    public async Task SearchAsync_RejectsAnswerWithoutSearchGrounding()
    {
        var handler = new DelegateHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                { "candidates": [{ "content": { "parts": [{ "text": "Непроверенный ответ." }] } }] }
                """, Encoding.UTF8, "application/json")
        }));
        var service = CreateService(handler);

        var result = await service.SearchAsync("что нового", null, CancellationToken.None);

        Assert.Equal("not_grounded", result.Status);
        Assert.Empty(result.Sources);
        Assert.Equal(0, result.QueryCount);
    }

    [Fact]
    public async Task SearchAsync_RejectsOversizedQueryBeforeProviderCall()
    {
        var handler = new DelegateHandler(_ => throw new Xunit.Sdk.XunitException("Provider must not be called"));
        var service = CreateService(handler);

        var result = await service.SearchAsync(new string('x', GroundedWebSearchService.MaxQueryLength + 1), null, CancellationToken.None);

        Assert.Equal("invalid", result.Status);
    }

    private static GroundedWebSearchService CreateService(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Gemini:ApiKey"] = "test-key" })
            .Build();
        return new GroundedWebSearchService(
            configuration,
            new TestHttpClientFactory(handler),
            NullLogger<GroundedWebSearchService>.Instance);
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            callback(request);
    }
}
