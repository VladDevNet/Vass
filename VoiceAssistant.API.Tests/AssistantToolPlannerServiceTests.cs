using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceAssistant.API.Services;
using Xunit;

namespace VoiceAssistant.API.Tests;

public class AssistantToolPlannerServiceTests
{
    [Fact]
    public async Task GenerateAsync_PreservesFullModelContentForAFunctionResponseRoundTrip()
    {
        string? requestBody = null;
        var handler = new DelegateHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            const string payload = """
                {
                  "candidates": [{
                    "content": {
                      "role": "model",
                      "parts": [
                        { "thought": true, "thoughtSignature": "opaque-provider-signature" },
                        { "functionCall": {
                          "name": "memory_search",
                          "id": "call-123",
                          "args": { "query": "космонавт" }
                        }}
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
        var planner = CreatePlanner(handler);
        var contents = new[]
        {
            JsonSerializer.SerializeToElement(new
            {
                role = "user",
                parts = new[] { new { text = "Найди, что я говорил о космосе" } }
            })
        };

        var result = await planner.GenerateAsync("system", contents, null, CancellationToken.None);

        Assert.True(result.ProviderAvailable);
        var call = Assert.Single(result.Calls);
        Assert.Equal("memory_search", call.Name);
        Assert.Equal("call-123", call.CallId);
        Assert.Equal("космонавт", call.Arguments.GetProperty("query").GetString());
        Assert.NotNull(result.ModelContent);
        Assert.Contains("thoughtSignature", result.ModelContent!.Value.GetRawText());
        Assert.Contains("functionDeclarations", requestBody!);
    }

    private static AssistantToolPlannerService CreatePlanner(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Gemini:ApiKey"] = "test-key" })
            .Build();
        return new AssistantToolPlannerService(
            configuration,
            new TestHttpClientFactory(handler),
            NullLogger<AssistantToolPlannerService>.Instance);
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
