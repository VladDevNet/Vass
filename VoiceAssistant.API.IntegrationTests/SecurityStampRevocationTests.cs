using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using VoiceAssistant.API.Controllers;
using VoiceAssistant.API.Data.Entities;
using Xunit;

namespace VoiceAssistant.API.IntegrationTests;

// PROJECT-AUDIT-2026-07-10 SEC-05: verifies the actual security property,
// not just that a token round-trips -- a token issued BEFORE a security
// stamp regeneration must stop working immediately after, even though its
// own exp claim hasn't been reached and its signature is still valid. This
// is the one thing that makes the stamp claim a real (if manual-trigger-
// only) revoke path rather than decoration. Own test class (own
// TestWebApplicationFactory instance, own "auth" rate-limit budget) rather
// than folding into AuthControllerTests, which is already at 8/10 of that
// budget.
public class SecurityStampRevocationTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public SecurityStampRevocationTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Token_AfterSecurityStampRegenerated_IsRejected()
    {
        var client = _factory.CreateClient();
        var email = $"revoke-user-{Guid.NewGuid():N}@example.com";
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new AuthController.RegisterRequest(email, "correct-password-123", "ru"));
        var token = (await register.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Sanity: the freshly issued token works before any revocation.
        var before = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        // Simulate a forced revoke (e.g. "log out everywhere" / compromised
        // account response) -- the ONLY server-side action this mechanism
        // requires, deliberately without any dedicated revoke endpoint or
        // UI (out of scope, see the plan doc).
        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var user = await userManager.FindByEmailAsync(email);
            Assert.NotNull(user);
            await userManager.UpdateSecurityStampAsync(user);
        }

        // The SAME token (unexpired, still validly signed) must now be
        // rejected -- the whole point of embedding the stamp.
        var after = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task Login_AfterSecurityStampRegenerated_IssuesFreshWorkingToken()
    {
        var client = _factory.CreateClient();
        var email = $"revoke-relogin-{Guid.NewGuid():N}@example.com";
        const string password = "correct-password-123";
        await client.PostAsJsonAsync("/api/v1/auth/register", new AuthController.RegisterRequest(email, password, "ru"));

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var user = await userManager.FindByEmailAsync(email);
            Assert.NotNull(user);
            await userManager.UpdateSecurityStampAsync(user);
        }

        // A fresh login issues a token embedding the NEW stamp -- revocation
        // isn't permanent account lockout, just an invalidation of tokens
        // issued before the regeneration point.
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new AuthController.LoginRequest(email, password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var newToken = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", newToken);
        var response = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
