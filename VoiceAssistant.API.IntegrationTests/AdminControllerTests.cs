using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VoiceAssistant.API.Controllers;
using VoiceAssistant.API.Data;
using VoiceAssistant.API.Data.Entities;
using VoiceAssistant.API.Services;

namespace VoiceAssistant.API.IntegrationTests;

public class AdminControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public AdminControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Users_NonAdmin_ReturnsForbidden()
    {
        var client = await RegisterClientAsync("regular");

        var response = await client.GetAsync("/api/v1/admin/users");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Users_Admin_ReturnsActivityAggregatesAndCanBlockUser()
    {
        var admin = await CreateAdminClientAsync();
        var regular = await RegisterClientAsync("managed");
        var regularEmail = regular.DefaultRequestHeaders.GetValues("X-Test-Email").Single();

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.SingleAsync(u => u.Email == regularEmail);
            var session = await db.ChatSessions.SingleAsync(s => s.UserId == user.Id);
            db.Messages.AddRange(
                new Message { ChatSessionId = session.Id, Role = "user", Content = "12345" },
                new Message { ChatSessionId = session.Id, Role = "assistant", Content = "1234567" });
            await db.SaveChangesAsync();
        }

        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/admin/users?search={Uri.EscapeDataString(regularEmail)}");
        var row = list.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(2, row.GetProperty("messageCount").GetInt64());
        Assert.Equal(12, row.GetProperty("characterCount").GetInt64());
        Assert.True(row.GetProperty("isApproved").GetBoolean());

        var userId = row.GetProperty("id").GetString();
        var blockResponse = await admin.PatchAsJsonAsync(
            $"/api/v1/admin/users/{userId}/approval",
            new AdminController.ApprovalRequest(false));
        Assert.Equal(HttpStatusCode.OK, blockResponse.StatusCode);

        var oldTokenResponse = await regular.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, oldTokenResponse.StatusCode);

        var loginResponse = await _factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/login",
            new AuthController.LoginRequest(regularEmail, "correct-password-123"));
        Assert.Equal(HttpStatusCode.Forbidden, loginResponse.StatusCode);
    }

    [Fact]
    public async Task Overview_Admin_ReturnsUserTotals()
    {
        var admin = await CreateAdminClientAsync();

        var response = await admin.GetAsync("/api/v1/admin/overview");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("totalUsers").GetInt32() >= 1);
        Assert.Equal(
            body.GetProperty("totalUsers").GetInt32(),
            body.GetProperty("approvedUsers").GetInt32() + body.GetProperty("pendingUsers").GetInt32());
    }

    private async Task<HttpClient> CreateAdminClientAsync()
    {
        var client = await RegisterClientAsync("admin");
        var email = client.DefaultRequestHeaders.GetValues("X-Test-Email").Single();

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            if (!await roleManager.RoleExistsAsync(AdminBootstrapper.RoleName))
            {
                Assert.True((await roleManager.CreateAsync(new IdentityRole(AdminBootstrapper.RoleName))).Succeeded);
            }

            var user = await userManager.FindByEmailAsync(email);
            Assert.NotNull(user);
            Assert.True((await userManager.AddToRoleAsync(user!, AdminBootstrapper.RoleName)).Succeeded);
        }

        var login = await _factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/login",
            new AuthController.LoginRequest(email, "correct-password-123"));
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<HttpClient> RegisterClientAsync(string prefix)
    {
        var client = _factory.CreateClient();
        var email = $"{prefix}-{Guid.NewGuid():N}@example.com";
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new AuthController.RegisterRequest(email, "correct-password-123", "ru"));
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("X-Test-Email", email);
        return client;
    }
}
