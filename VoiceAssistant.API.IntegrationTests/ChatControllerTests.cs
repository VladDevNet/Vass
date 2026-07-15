using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using VoiceAssistant.API.Controllers;
using VoiceAssistant.API.Data;
using VoiceAssistant.API.Data.Entities;
using VoiceAssistant.API.Services;
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
        var registerBody = await register.Content.ReadAsStringAsync();
        if (!register.IsSuccessStatusCode || string.IsNullOrWhiteSpace(registerBody))
            throw new InvalidOperationException($"Test registration failed: {(int)register.StatusCode} {registerBody}");
        var token = JsonDocument.Parse(registerBody).RootElement.GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task GetSessions_Authenticated_ReturnsSessionCreatedAtRegistration()
    {
        var client = await CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/v1/chat/sessions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var sessions = body.EnumerateArray().ToList();
        Assert.Single(sessions);
        var id = sessions[0].GetProperty("id").GetInt32();
        Assert.True(id > 0);

        // GetSessions is a pure read (PROJECT-AUDIT-2026-07-10 section 6) --
        // calling it again must return the SAME session, not create another.
        var second = await client.GetFromJsonAsync<JsonElement>("/api/v1/chat/sessions");
        var secondSessions = second.EnumerateArray().ToList();
        Assert.Single(secondSessions);
        Assert.Equal(id, secondSessions[0].GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task GetSessions_NoSessions_ReturnsEmptyArray()
    {
        var client = await CreateAuthenticatedClientAsync();
        var sessionResponse = await client.GetAsync("/api/v1/chat/sessions");
        var sessionId = (await sessionResponse.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().First().GetProperty("id").GetInt32();

        var deleteResponse = await client.DeleteAsync($"/api/v1/chat/sessions/{sessionId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var response = await client.GetAsync("/api/v1/chat/sessions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(body.EnumerateArray());
    }

    [Fact]
    public async Task GetSessions_Unauthenticated_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/chat/sessions");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Send_TextMessage_StreamsReplyAndPersistsBothMessages()
    {
        var client = await CreateAuthenticatedClientAsync();
        var sessionResponse = await client.GetAsync("/api/v1/chat/sessions");
        var sessionId = (await sessionResponse.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().First().GetProperty("id").GetInt32();

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
    public async Task Send_IgnoresLegacyEmptyAssistantMessageInConversationHistory()
    {
        var client = await CreateAuthenticatedClientAsync();
        var sessionResponse = await client.GetAsync("/api/v1/chat/sessions");
        var sessionId = (await sessionResponse.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().First().GetProperty("id").GetInt32();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Messages.Add(new Message { ChatSessionId = sessionId, Role = "assistant", Content = "" });
            await db.SaveChangesAsync();
        }

        var sendResponse = await client.PostAsJsonAsync("/api/v1/chat/send",
            new ChatController.SendRequest(sessionId, "Продолжим разговор"));

        Assert.Equal(HttpStatusCode.OK, sendResponse.StatusCode);
        var raw = await sendResponse.Content.ReadAsStringAsync();
        Assert.Contains("data: [DONE]", raw);
        Assert.DoesNotContain("Text content cannot be empty", raw);
    }

    [Fact]
    public async Task Send_ExternalActionsSupported_StreamsTypedYouTubeAction()
    {
        var client = await CreateAuthenticatedClientAsync();
        var sessionResponse = await client.GetAsync("/api/v1/chat/sessions");
        var sessionId = (await sessionResponse.Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateArray().First().GetProperty("id").GetInt32();

        var sendResponse = await client.PostAsJsonAsync("/api/v1/chat/send",
            new ChatController.SendRequest(
                sessionId,
                "Открой в YouTube песни Высоцкого",
                SupportsExternalActions: true));

        Assert.Equal(HttpStatusCode.OK, sendResponse.StatusCode);
        var raw = await sendResponse.Content.ReadAsStringAsync();
        var actionLine = raw.Split('\n')
            .First(line => line.StartsWith("data: ") && line.Contains("externalAction", StringComparison.Ordinal));
        using var actionJson = JsonDocument.Parse(actionLine[6..]);
        var action = actionJson.RootElement.GetProperty("externalAction");
        Assert.Equal(ExternalActionTypes.YouTubeSearch, action.GetProperty("type").GetString());
        Assert.Equal("external", action.GetProperty("taxonomy").GetString());
        Assert.Equal("песни Высоцкого", action.GetProperty("query").GetString());
        Assert.Equal(JsonValueKind.Null, action.GetProperty("videoId").ValueKind);
        var actionId = action.GetProperty("actionId").GetGuid();

        var dispatched = await client.PostAsJsonAsync("/api/v1/actions/receipts",
            new ActionsController.RecordReceiptRequest(actionId, ActionReceiptStatuses.HandlerDispatched, "external_handler_dispatched"));
        dispatched.EnsureSuccessStatusCode();
        var receipt = await dispatched.Content.ReadFromJsonAsync<ActionReceiptResponse>();
        Assert.Equal(ActionReceiptStatuses.HandlerDispatched, receipt!.Status);
        Assert.Equal("external_handler_dispatched", receipt.ResultCode);

        // A mobile retry of the exact terminal receipt is safe, but a later
        // contradictory receipt cannot turn a dispatched handler into failure.
        var duplicate = await client.PostAsJsonAsync("/api/v1/actions/receipts",
            new ActionsController.RecordReceiptRequest(actionId, ActionReceiptStatuses.HandlerDispatched, "external_handler_dispatched"));
        duplicate.EnsureSuccessStatusCode();
        var conflicting = await client.PostAsJsonAsync("/api/v1/actions/receipts",
            new ActionsController.RecordReceiptRequest(actionId, ActionReceiptStatuses.Failed, "external_handler_failed"));
        Assert.Equal(HttpStatusCode.NotFound, conflicting.StatusCode);
        Assert.Contains("data: [DONE]", raw);
    }

    [Fact]
    public async Task Send_SharedYouTubeLink_IsIncludedWithTheVoicePromptAndCanLaunchTheVideo()
    {
        var client = await CreateAuthenticatedClientAsync();
        var sessionResponse = await client.GetAsync("/api/v1/chat/sessions");
        var sessionId = (await sessionResponse.Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateArray().First().GetProperty("id").GetInt32();

        var response = await client.PostAsJsonAsync("/api/v1/chat/send",
            new ChatController.SendRequest(
                sessionId,
                "Запусти это видео",
                SupportsExternalActions: true,
                SharedContent: "https://www.youtube.com/watch?v=bWGXT5wjkd4"));

        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync();
        var actionLine = raw.Split('\n')
            .First(line => line.StartsWith("data: ") && line.Contains("externalAction", StringComparison.Ordinal));
        using var actionJson = JsonDocument.Parse(actionLine[6..]);
        Assert.Equal(ExternalActionTypes.YouTubeWatch,
            actionJson.RootElement.GetProperty("externalAction").GetProperty("type").GetString());

        var session = await client.GetFromJsonAsync<JsonElement>($"/api/v1/chat/sessions/{sessionId}");
        var userMessage = session.GetProperty("messages").EnumerateArray().First();
        Assert.Contains("Запусти это видео", userMessage.GetProperty("content").GetString());
        Assert.Contains("https://www.youtube.com/watch?v=bWGXT5wjkd4", userMessage.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Send_RememberCommandInsideSpeech_StoresTheMostRecentSharedLink()
    {
        var client = await CreateAuthenticatedClientAsync();
        var sessionResponse = await client.GetAsync("/api/v1/chat/sessions");
        var sessionId = (await sessionResponse.Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateArray().First().GetProperty("id").GetInt32();
        const string videoUrl = "https://youtube.com/watch?v=tbT2ogwa49Y";

        var shareResponse = await client.PostAsJsonAsync("/api/v1/chat/send",
            new ChatController.SendRequest(sessionId, "Посмотри эту ссылку", SharedContent: videoUrl));
        shareResponse.EnsureSuccessStatusCode();

        var rememberResponse = await client.PostAsJsonAsync("/api/v1/chat/send",
            new ChatController.SendRequest(sessionId,
                "Ну стой, это важно. Запомни мне, пожалуйста, эту ссылку."));
        rememberResponse.EnsureSuccessStatusCode();
        Assert.Contains("data: [DONE]", await rememberResponse.Content.ReadAsStringAsync());

        var memory = await client.GetFromJsonAsync<JsonElement>("/api/v1/memory/items");
        Assert.Contains(memory.EnumerateArray(), item =>
            item.GetProperty("text").GetString() == videoUrl);
    }

    [Fact]
    public async Task Send_PutIntoLongTermMemory_ReturnsDurableReceiptWithoutModelReply()
    {
        var client = await CreateAuthenticatedClientAsync();
        var sessionResponse = await client.GetAsync("/api/v1/chat/sessions");
        var sessionId = (await sessionResponse.Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateArray().First().GetProperty("id").GetInt32();

        var response = await client.PostAsJsonAsync("/api/v1/chat/send",
            new ChatController.SendRequest(sessionId,
                "Ну ладно, давай-ка попробуем положить в долгосрочную память, не знаю, возраст Белла, там 15 лет."));

        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync();
        var receiptLine = raw.Split('\n')
            .First(line => line.StartsWith("data: ") && line.Contains("text", StringComparison.Ordinal));
        using var receiptJson = JsonDocument.Parse(receiptLine[6..]);
        Assert.Equal("Сохранено в долгосрочную память.", receiptJson.RootElement.GetProperty("text").GetString());
        Assert.Contains("data: [DONE]", raw);
        Assert.DoesNotContain(FakeGeminiHandler.DefaultReplyText, raw);

        var memory = await client.GetFromJsonAsync<JsonElement>("/api/v1/memory/items");
        Assert.Contains(memory.EnumerateArray(), item =>
            item.GetProperty("text").GetString() == "возраст Белла, там 15 лет");
    }

    [Fact]
    public async Task Send_ScreenAnalysisCapable_RequestsCaptureBeforePersistingMessage()
    {
        var client = await CreateAuthenticatedClientAsync();
        var sessionResponse = await client.GetAsync("/api/v1/chat/sessions");
        var sessionId = (await sessionResponse.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().First().GetProperty("id").GetInt32();

        var response = await client.PostAsJsonAsync("/api/v1/chat/send",
            new ChatController.SendRequest(sessionId, "Объясни, что сейчас на экране", SupportsScreenAnalysis: true));

        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("screenCapture", raw);
        Assert.Contains("data: [DONE]", raw);
        Assert.DoesNotContain(FakeGeminiHandler.DefaultReplyText, raw);

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/chat/sessions/{sessionId}");
        Assert.Empty(detail.GetProperty("messages").EnumerateArray());
    }

    [Fact]
    public async Task Send_LaunchFollowUp_UsesVideoFromPreviousAssistantReply()
    {
        var client = await CreateAuthenticatedClientAsync();
        var sessionResponse = await client.GetAsync("/api/v1/chat/sessions");
        var sessionId = (await sessionResponse.Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateArray().First().GetProperty("id").GetInt32();

        var discoveryResponse = await client.PostAsJsonAsync("/api/v1/chat/send",
            new ChatController.SendRequest(sessionId, "Подбери конкретный ролик", SupportsExternalActions: false));
        discoveryResponse.EnsureSuccessStatusCode();

        var launchResponse = await client.PostAsJsonAsync("/api/v1/chat/send",
            new ChatController.SendRequest(sessionId, "Запускай. Посмотрим.", SupportsExternalActions: true));
        launchResponse.EnsureSuccessStatusCode();
        var raw = await launchResponse.Content.ReadAsStringAsync();
        var actionLine = raw.Split('\n')
            .First(line => line.StartsWith("data: ") && line.Contains("externalAction", StringComparison.Ordinal));
        using var actionJson = JsonDocument.Parse(actionLine[6..]);
        var action = actionJson.RootElement.GetProperty("externalAction");

        Assert.Equal(ExternalActionTypes.YouTubeWatch, action.GetProperty("type").GetString());
        Assert.Equal("bWGXT5wjkd4", action.GetProperty("videoId").GetString());
    }

    [Fact]
    public async Task Send_EmptyMessageNoAudio_ReturnsBadRequest()
    {
        var client = await CreateAuthenticatedClientAsync();
        var sessionResponse = await client.GetAsync("/api/v1/chat/sessions");
        var sessionId = (await sessionResponse.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().First().GetProperty("id").GetInt32();

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
