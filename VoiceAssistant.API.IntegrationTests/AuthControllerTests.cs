using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using VoiceAssistant.API.Controllers;
using Xunit;

namespace VoiceAssistant.API.IntegrationTests;

public class AuthControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client;

    public AuthControllerTests(TestWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    private static string UniqueEmail() => $"auth-user-{Guid.NewGuid():N}@example.com";

    [Fact]
    public async Task Register_NewUser_ReturnsToken()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auth/register",
            new AuthController.RegisterRequest(UniqueEmail(), "correct-password-123", "ru"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("token").GetString()));
    }

    [Fact]
    public async Task Register_DuplicateEmail_ReturnsBadRequest()
    {
        var email = UniqueEmail();
        var first = await _client.PostAsJsonAsync("/api/v1/auth/register",
            new AuthController.RegisterRequest(email, "correct-password-123", "ru"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await _client.PostAsJsonAsync("/api/v1/auth/register",
            new AuthController.RegisterRequest(email, "another-password-456", "ru"));

        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task Login_CorrectPassword_ReturnsToken()
    {
        var email = UniqueEmail();
        const string password = "correct-password-123";
        await _client.PostAsJsonAsync("/api/v1/auth/register", new AuthController.RegisterRequest(email, password, "ru"));

        var response = await _client.PostAsJsonAsync("/api/v1/auth/login", new AuthController.LoginRequest(email, password));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("token").GetString()));
    }

    [Fact]
    public async Task Login_WrongPassword_ReturnsUnauthorized()
    {
        var email = UniqueEmail();
        await _client.PostAsJsonAsync("/api/v1/auth/register", new AuthController.RegisterRequest(email, "correct-password-123", "ru"));

        var response = await _client.PostAsJsonAsync("/api/v1/auth/login", new AuthController.LoginRequest(email, "wrong-password"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_WithValidToken_ReturnsOwnEmail()
    {
        var email = UniqueEmail();
        var register = await _client.PostAsJsonAsync("/api/v1/auth/register",
            new AuthController.RegisterRequest(email, "correct-password-123", "ru"));
        var token = (await register.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(email, body.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Me_WithoutToken_ReturnsUnauthorized()
    {
        var response = await _client.GetAsync("/api/v1/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
