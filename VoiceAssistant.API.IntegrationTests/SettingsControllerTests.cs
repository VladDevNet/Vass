using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using VoiceAssistant.API.Controllers;
using Xunit;

namespace VoiceAssistant.API.IntegrationTests;

public class SettingsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public SettingsControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();
        var email = $"settings-user-{Guid.NewGuid():N}@example.com";
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new AuthController.RegisterRequest(email, "correct-password-123", "ru"));
        var token = (await register.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Get_NewUser_ReturnsDefaults()
    {
        var client = await CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/v1/settings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("uk", body.GetProperty("interfaceLanguage").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("openAiApiKey").ValueKind);
    }

    [Fact]
    public async Task Put_ValidDisplayName_PersistsAndRoundTrips()
    {
        var client = await CreateAuthenticatedClientAsync();

        var update = await client.PutAsJsonAsync("/api/v1/settings",
            new SettingsController.SettingsUpdateRequest("Влад", null, null, null, null, null, null, null, null));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var get = await client.GetFromJsonAsync<JsonElement>("/api/v1/settings");
        Assert.Equal("Влад", get.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Put_OversizedDisplayName_ReturnsBadRequest()
    {
        var client = await CreateAuthenticatedClientAsync();
        var tooLong = new string('a', 101);

        var response = await client.PutAsJsonAsync("/api/v1/settings",
            new SettingsController.SettingsUpdateRequest(tooLong, null, null, null, null, null, null, null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_InvalidAvatarId_ReturnsBadRequest()
    {
        var client = await CreateAuthenticatedClientAsync();

        var response = await client.PutAsJsonAsync("/api/v1/settings",
            new SettingsController.SettingsUpdateRequest(null, null, "not-a-real-avatar", null, null, null, null, null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_GeminiApiKey_ResponseReturnsMaskedNotPlaintext()
    {
        var client = await CreateAuthenticatedClientAsync();
        const string realKey = "AIzaSyTestFakeKeyForIntegrationTestsOnly123";

        var update = await client.PutAsJsonAsync("/api/v1/settings",
            new SettingsController.SettingsUpdateRequest(null, null, null, null, null, null, realKey, null, null));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var body = await update.Content.ReadFromJsonAsync<JsonElement>();
        var masked = body.GetProperty("geminiApiKey").GetString();
        Assert.NotNull(masked);
        Assert.NotEqual(realKey, masked);
        Assert.Contains("...", masked);

        // GET afterwards must also return the masked form, never the plaintext
        // key round-tripping back out (PROJECT-AUDIT-2026-07-10 SEC-03).
        var get = await client.GetFromJsonAsync<JsonElement>("/api/v1/settings");
        Assert.Equal(masked, get.GetProperty("geminiApiKey").GetString());
    }

    [Fact]
    public async Task Get_Unauthenticated_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/settings");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
