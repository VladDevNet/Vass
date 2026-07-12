using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using VoiceAssistant.API.Controllers;
using Xunit;

namespace VoiceAssistant.API.IntegrationTests;

// Each [Fact] registers its own user via CreateAuthenticatedClientAsync
// (register isn't rate-limited by IP-shared-bucket concerns across DIFFERENT
// TestWebApplicationFactory instances -- see AuthControllerTests' own note
// -- but within THIS class's one shared factory/bucket, 9 facts x 1 register
// call each = 9/10 of Program.cs's "auth" policy budget). Any new [Fact]
// added here needs to account for that -- either reuse an existing
// authenticated client where the test doesn't need fully isolated state, or
// split into a second test class (own factory, own fresh budget).
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
    public async Task Patch_ValidDisplayName_PersistsAndRoundTrips()
    {
        var client = await CreateAuthenticatedClientAsync();

        var update = await client.PatchAsJsonAsync("/api/v1/settings",
            new SettingsController.SettingsUpdateRequest("Влад", null, null, null, null, null, null, null, null));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var get = await client.GetFromJsonAsync<JsonElement>("/api/v1/settings");
        Assert.Equal("Влад", get.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Patch_OversizedDisplayName_ReturnsBadRequest()
    {
        var client = await CreateAuthenticatedClientAsync();
        var tooLong = new string('a', 101);

        var response = await client.PatchAsJsonAsync("/api/v1/settings",
            new SettingsController.SettingsUpdateRequest(tooLong, null, null, null, null, null, null, null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Patch_InvalidAvatarId_ReturnsBadRequest()
    {
        var client = await CreateAuthenticatedClientAsync();

        var response = await client.PatchAsJsonAsync("/api/v1/settings",
            new SettingsController.SettingsUpdateRequest(null, null, "not-a-real-avatar", null, null, null, null, null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Patch_GeminiApiKey_ResponseReturnsMaskedNotPlaintext()
    {
        var client = await CreateAuthenticatedClientAsync();
        const string realKey = "AIzaSyTestFakeKeyForIntegrationTestsOnly123";

        var update = await client.PatchAsJsonAsync("/api/v1/settings",
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

    // PROJECT-AUDIT-2026-07-10 API-01: the actual lost-update property PATCH
    // semantics exist to close -- a PATCH touching only displayName must
    // leave a DIFFERENT field, set by an earlier independent PATCH, exactly
    // as it was. Under the old whole-object PUT contract this would have
    // silently wiped assistantName back to null.
    [Fact]
    public async Task Patch_TouchingOneField_LeavesOtherFieldsUntouched()
    {
        var client = await CreateAuthenticatedClientAsync();

        await client.PatchAsJsonAsync("/api/v1/settings",
            new SettingsController.SettingsUpdateRequest(null, "Ольга", null, null, null, null, null, null, null));

        var response = await client.PatchAsJsonAsync("/api/v1/settings",
            new SettingsController.SettingsUpdateRequest("Влад", null, null, null, null, null, null, null, null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Влад", body.GetProperty("displayName").GetString());
        Assert.Equal("Ольга", body.GetProperty("assistantName").GetString());
    }

    // Empty string is the explicit "clear this field" signal, distinct from
    // null/omitted ("leave it alone") -- see SettingsController.Patch's own
    // comment for why.
    [Fact]
    public async Task Patch_EmptyStringOnAssistantName_ClearsItToNull()
    {
        var client = await CreateAuthenticatedClientAsync();
        await client.PatchAsJsonAsync("/api/v1/settings",
            new SettingsController.SettingsUpdateRequest(null, "Ольга", null, null, null, null, null, null, null));

        var response = await client.PatchAsJsonAsync("/api/v1/settings",
            new SettingsController.SettingsUpdateRequest(null, "", null, null, null, null, null, null, null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, body.GetProperty("assistantName").ValueKind);
    }

    // PROJECT-AUDIT-2026-07-10 API-01's masked-key ambiguity fix: the OLD
    // check (`Contains("...")`) would have wrongly treated ANY key
    // containing the literal substring "..." as an already-masked
    // placeholder to be skipped, silently discarding it. This key is
    // constructed to contain "..." while being a genuinely new value (no
    // key was ever set on this fresh user) -- it must still be saved.
    [Fact]
    public async Task Patch_NewKeyContainingDotDotDot_IsSavedNotTreatedAsMaskedPlaceholder()
    {
        var client = await CreateAuthenticatedClientAsync();
        const string trickyKey = "sk-fake...key-with-dots-999";

        var response = await client.PatchAsJsonAsync("/api/v1/settings",
            new SettingsController.SettingsUpdateRequest(null, null, null, null, null, null, trickyKey, null, null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        // MaskKey(key) == key[..3] + "..." + key[^4..] -- proves the real
        // value was actually stored and masked, not silently dropped.
        Assert.Equal("sk-...-999", body.GetProperty("geminiApiKey").GetString());
    }

    // Echoing the masked placeholder straight back (as a client naively
    // round-tripping a GET response through a PATCH might) must be a no-op
    // on that field, not overwrite the real key with the placeholder text
    // itself.
    [Fact]
    public async Task Patch_EchoingMaskedPlaceholderBack_DoesNotChangeUnderlyingKey()
    {
        var client = await CreateAuthenticatedClientAsync();
        const string realKey = "AIzaSyEchoTestRealKeyValue000000000111";

        var first = await client.PatchAsJsonAsync("/api/v1/settings",
            new SettingsController.SettingsUpdateRequest(null, null, null, null, null, null, realKey, null, null));
        var maskedAfterFirstSave = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("geminiApiKey").GetString();

        var second = await client.PatchAsJsonAsync("/api/v1/settings",
            new SettingsController.SettingsUpdateRequest("Влад", null, null, null, null, null, maskedAfterFirstSave, null, null));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Влад", body.GetProperty("displayName").GetString());
        Assert.Equal(maskedAfterFirstSave, body.GetProperty("geminiApiKey").GetString());
    }

    [Fact]
    public async Task Get_Unauthenticated_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/settings");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
