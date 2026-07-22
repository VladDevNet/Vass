using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceAssistant.API.Services;
using Xunit;

namespace VoiceAssistant.API.Tests;

public class OpenAiResponsesServiceTests
{
    [Fact]
    public async Task StreamResponseAsync_UsesResponsesSseAndPreservesImageInput()
    {
        string? requestBody = null;
        var handler = new DelegateHandler(async request =>
        {
            Assert.Equal("https://api.openai.com/v1/responses", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization.Parameter);
            requestBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Готово\"}\n\n" +
                    "data: {\"type\":\"response.completed\"}\n\n",
                    Encoding.UTF8,
                    "text/event-stream")
            };
        });
        var service = CreateService(handler);

        var chunks = new List<string>();
        await foreach (var chunk in service.StreamResponseAsync("system", [
            new GeminiMessage("user", [
                new GeminiPart(Text: "Что на изображении?"),
                new GeminiPart(MimeType: "image/jpeg", Data: [0xFF, 0xD8, 0xFF])
            ])
        ], 256, CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(["Готово"], chunks);
        using var document = JsonDocument.Parse(requestBody!);
        Assert.Equal("gpt-5.6", document.RootElement.GetProperty("model").GetString());
        Assert.Equal("high", document.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.True(document.RootElement.GetProperty("stream").GetBoolean());
        var content = document.RootElement.GetProperty("input")[0].GetProperty("content");
        Assert.Equal("input_text", content[0].GetProperty("type").GetString());
        Assert.Equal("input_image", content[1].GetProperty("type").GetString());
        Assert.StartsWith("data:image/jpeg;base64,", content[1].GetProperty("image_url").GetString());
    }

    [Fact]
    public void BuildInput_PdfAttachment_UsesInputFileDataUrl()
    {
        var input = OpenAiResponsesService.BuildInput([
            new GeminiMessage("user", [
                new GeminiPart(Text: "Прочитай файл"),
                new GeminiPart(MimeType: "application/pdf", Data: [1, 2, 3])
            ])
        ]);

        var content = Assert.Single(input).GetProperty("content");
        Assert.Equal("input_file", content[1].GetProperty("type").GetString());
        Assert.Equal("attachment.pdf", content[1].GetProperty("filename").GetString());
        Assert.StartsWith("data:application/pdf;base64,", content[1].GetProperty("file_data").GetString());
    }

    [Fact]
    public async Task CreateResponseAsync_ReturnsNativeFunctionCall()
    {
        var handler = new DelegateHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"output":[{"type":"function_call","name":"memory_search","arguments":"{\"query\":\"космос\"}","call_id":"call-123"}]}
                """, Encoding.UTF8, "application/json")
        }));
        var service = CreateService(handler);

        var response = await service.CreateResponseAsync(
            "system",
            [JsonSerializer.SerializeToElement(new { role = "user", content = "Найди космос" })],
            [JsonSerializer.SerializeToElement(new { type = "function", name = "memory_search", parameters = new { type = "object" } })],
            256,
            CancellationToken.None);

        var call = Assert.Single(response.FunctionCalls);
        Assert.Equal("memory_search", call.Name);
        Assert.Equal("call-123", call.CallId);
        Assert.Equal("{\"query\":\"космос\"}", call.Arguments);
    }

    [Theory]
    [InlineData("openai", PrimaryModelSettings.OpenAi)]
    [InlineData("gemini", PrimaryModelSettings.Gemini)]
    [InlineData("unexpected", PrimaryModelSettings.Gemini)]
    public void Provider_OnlyOpenAiSelectsOpenAi(string configured, string expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["PrimaryModel:Provider"] = configured })
            .Build();

        Assert.Equal(expected, PrimaryModelSettings.Provider(configuration));
    }

    private static OpenAiResponsesService CreateService(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenAI:ApiKey"] = "test-key",
                ["OpenAI:Model"] = "gpt-5.6",
                ["OpenAI:ReasoningEffort"] = "high"
            })
            .Build();
        return new OpenAiResponsesService(configuration, new TestHttpClientFactory(handler), NullLogger<OpenAiResponsesService>.Instance);
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
