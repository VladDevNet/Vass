using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using VoiceAssistant.API.Controllers;
using Xunit;

namespace VoiceAssistant.API.IntegrationTests;

public class VisualAssetsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public VisualAssetsControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();
        var email = $"visual-user-{Guid.NewGuid():N}@example.com";
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new AuthController.RegisterRequest(email, "correct-password-123", "ru"));
        var token = (await register.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static MultipartFormDataContent ImageUpload(byte[]? bytes = null)
    {
        var content = new MultipartFormDataContent();
        var image = new ByteArrayContent(bytes ?? [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10]);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(image, "file", "photo.jpg");
        return content;
    }

    [Fact]
    public async Task UploadReadDelete_OwnerCanManagePendingAsset()
    {
        var client = await CreateAuthenticatedClientAsync();
        var bytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };

        using var upload = ImageUpload(bytes);
        var uploadResponse = await client.PostAsync("/api/v1/chat/visual-assets", upload);

        Assert.Equal(HttpStatusCode.OK, uploadResponse.StatusCode);
        var uploaded = await uploadResponse.Content.ReadFromJsonAsync<JsonElement>();
        var id = uploaded.GetProperty("id").GetGuid();
        Assert.Equal("image/jpeg", uploaded.GetProperty("mimeType").GetString());

        var readResponse = await client.GetAsync($"/api/v1/chat/visual-assets/{id}/content");
        Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);
        Assert.Equal(bytes, await readResponse.Content.ReadAsByteArrayAsync());

        var deleteResponse = await client.DeleteAsync($"/api/v1/chat/visual-assets/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/chat/visual-assets/{id}/content")).StatusCode);
    }

    [Fact]
    public async Task VisualAsset_ForeignUserCannotReadOrAttach()
    {
        var owner = await CreateAuthenticatedClientAsync();
        var other = await CreateAuthenticatedClientAsync();
        using var upload = ImageUpload();
        var uploaded = await owner.PostAsync("/api/v1/chat/visual-assets", upload);
        var id = (await uploaded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/chat/visual-assets/{id}/content")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/v1/chat/visual-assets/{id}")).StatusCode);

        var sessions = await other.GetFromJsonAsync<JsonElement>("/api/v1/chat/sessions");
        var sessionId = sessions.EnumerateArray().First().GetProperty("id").GetInt32();
        var send = await other.PostAsJsonAsync("/api/v1/chat/send",
            new ChatController.SendRequest(sessionId, "Что на картинке?", VisualAssetId: id));
        Assert.Equal(HttpStatusCode.BadRequest, send.StatusCode);
    }

    [Fact]
    public async Task Send_WithVisualAsset_PersistsAuthenticatedAttachment()
    {
        var client = await CreateAuthenticatedClientAsync();
        using var upload = ImageUpload();
        var uploaded = await client.PostAsync("/api/v1/chat/visual-assets", upload);
        var id = (await uploaded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var sessions = await client.GetFromJsonAsync<JsonElement>("/api/v1/chat/sessions");
        var sessionId = sessions.EnumerateArray().First().GetProperty("id").GetInt32();

        var send = await client.PostAsJsonAsync("/api/v1/chat/send",
            new ChatController.SendRequest(sessionId, "Что на картинке?", VisualAssetId: id));
        Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        Assert.Contains("data: [DONE]", await send.Content.ReadAsStringAsync());

        var history = await client.GetFromJsonAsync<JsonElement>($"/api/v1/chat/sessions/{sessionId}");
        var message = history.GetProperty("messages").EnumerateArray().First();
        var attachment = message.GetProperty("attachments").EnumerateArray().Single();
        Assert.Equal(id, attachment.GetProperty("id").GetGuid());
        Assert.Equal("image", attachment.GetProperty("kind").GetString());

        var delete = await client.DeleteAsync($"/api/v1/chat/visual-assets/{id}");
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
    }
}
