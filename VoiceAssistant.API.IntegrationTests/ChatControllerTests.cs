using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using VoiceAssistant.API.Controllers;
using Xunit;

namespace VoiceAssistant.API.IntegrationTests;

public class ChatControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ChatControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();
        var email = $"chat-user-{Guid.NewGuid():N}@example.com";
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new AuthController.RegisterRequest(email, "correct-password-123", "ru"));
        var token = (await register.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task CreateSession_Authenticated_ReturnsNewSession()
    {
        var client = await CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/v1/chat/sessions", new ChatController.CreateSessionRequest(null, null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("id").GetInt32() > 0);
    }

    [Fact]
    public async Task CreateSession_Unauthenticated_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/chat/sessions", new ChatController.CreateSessionRequest(null, null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Send_TextMessage_StreamsReplyAndPersistsBothMessages()
    {
        var client = await CreateAuthenticatedClientAsync();
        var sessionResponse = await client.PostAsJsonAsync("/api/v1/chat/sessions", new ChatController.CreateSessionRequest(null, null));
        var sessionId = (await sessionResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var sendResponse = await client.PostAsJsonAsync("/api/v1/chat/send",
            new ChatController.SendRequest(sessionId, "Привет, как дела?", null));

        Assert.Equal(HttpStatusCode.OK, sendResponse.StatusCode);
        var raw = await sendResponse.Content.ReadAsStringAsync();
        // Not asserting the reply text literally appears here: System.Text.Json's
        // default encoder \uXXXX-escapes non-ASCII characters in the raw SSE
        // "data:" chunks, so Cyrillic text never appears unescaped in this
        // stream even though it round-trips correctly everywhere it's actually
        // deserialized (checked below via the persisted messages instead).
        Assert.Contains("data: [DONE]", raw);
        Assert.Contains("\"stats\"", raw);

        var sessionDetail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/chat/sessions/{sessionId}");
        var messages = sessionDetail.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(2, messages.Count);
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("Привет, как дела?", messages[0].GetProperty("content").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal(FakeGeminiHandler.DefaultReplyText, messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Send_EmptyMessageNoAudio_ReturnsBadRequest()
    {
        var client = await CreateAuthenticatedClientAsync();
        var sessionResponse = await client.PostAsJsonAsync("/api/v1/chat/sessions", new ChatController.CreateSessionRequest(null, null));
        var sessionId = (await sessionResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var sendResponse = await client.PostAsJsonAsync("/api/v1/chat/send",
            new ChatController.SendRequest(sessionId, "", null));

        Assert.Equal(HttpStatusCode.BadRequest, sendResponse.StatusCode);
    }

    [Fact]
    public async Task Send_UnknownSession_ReturnsNotFound()
    {
        var client = await CreateAuthenticatedClientAsync();

        var sendResponse = await client.PostAsJsonAsync("/api/v1/chat/send",
            new ChatController.SendRequest(999_999, "Привет", null));

        Assert.Equal(HttpStatusCode.NotFound, sendResponse.StatusCode);
    }
}
