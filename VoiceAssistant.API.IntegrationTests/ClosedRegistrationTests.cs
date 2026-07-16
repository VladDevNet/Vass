using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using VoiceAssistant.API.Controllers;
using Xunit;

namespace VoiceAssistant.API.IntegrationTests;

public sealed class ClosedRegistrationTests : IAsyncLifetime
{
    private readonly TestWebApplicationFactory _factory = new(registrationAutoApprove: false);
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _factory.InitializeAsync();
        _client = _factory.CreateClient();
    }

    public Task DisposeAsync() => ((IAsyncLifetime)_factory).DisposeAsync();

    [Fact]
    public async Task Register_WhenApprovalIsClosed_ReturnsPendingReceipt_AndLoginHasStableCode()
    {
        var email = $"closed-registration-{Guid.NewGuid():N}@example.com";
        const string password = "correct-password-123";

        var register = await _client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new AuthController.RegisterRequest(email, password, "ru"));

        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        var registerBody = await register.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(registerBody.GetProperty("approvalRequired").GetBoolean());
        Assert.False(registerBody.TryGetProperty("token", out var token) && !string.IsNullOrWhiteSpace(token.GetString()));

        var login = await _client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new AuthController.LoginRequest(email, password));

        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        var loginBody = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("approval_required", loginBody.GetProperty("code").GetString());
        Assert.True(loginBody.GetProperty("approvalRequired").GetBoolean());
    }
}
