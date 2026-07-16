using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Pgvector;
using VoiceAssistant.API.Controllers;
using VoiceAssistant.API.Data;
using VoiceAssistant.API.Data.Entities;
using VoiceAssistant.API.Services;
using Xunit;

namespace VoiceAssistant.API.IntegrationTests;

public class MemoryControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public MemoryControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Facts_AreIsolatedByUser_AndOwnerCanDeleteThem()
    {
        var first = await RegisterClientAsync("memory-first");
        var second = await RegisterClientAsync("memory-second");
        var firstFactId = await SeedFactAsync(first.Email, "Пользователь любит зелёный чай");
        var secondFactId = await SeedFactAsync(second.Email, "Пользователь любит кофе");

        var response = await first.Client.GetAsync("/api/v1/memory/facts");
        response.EnsureSuccessStatusCode();
        var facts = await response.Content.ReadFromJsonAsync<List<MemoryController.MemoryFactResponse>>();

        var fact = Assert.Single(facts!);
        Assert.Equal(firstFactId, fact.Id);
        Assert.Equal("Пользователь любит зелёный чай", fact.Fact);

        var foreignDelete = await first.Client.DeleteAsync($"/api/v1/memory/facts/{secondFactId}");
        Assert.Equal(HttpStatusCode.NotFound, foreignDelete.StatusCode);

        var ownDelete = await first.Client.DeleteAsync($"/api/v1/memory/facts/{firstFactId}");
        Assert.Equal(HttpStatusCode.NoContent, ownDelete.StatusCode);
        Assert.Empty((await first.Client.GetFromJsonAsync<List<MemoryController.MemoryFactResponse>>(
            "/api/v1/memory/facts"))!);
    }

    [Fact]
    public async Task LegacyDeleteAll_IsRejectedBecauseClearNeedsConfirmation()
    {
        var first = await RegisterClientAsync("memory-clear-first");
        await SeedFactAsync(first.Email, "Факт первого пользователя");
        await SeedFactAsync(first.Email, "Ещё один факт первого пользователя");

        var response = await first.Client.DeleteAsync("/api/v1/memory/facts");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(2, (await first.Client.GetFromJsonAsync<List<MemoryController.MemoryFactResponse>>(
            "/api/v1/memory/facts"))!.Count);
    }

    [Fact]
    public async Task VerifiedRemember_IsIdempotent_AndMemoryItemsStayPrivate()
    {
        var first = await RegisterClientAsync("verified-memory-first");
        var second = await RegisterClientAsync("verified-memory-second");
        var operationId = Guid.NewGuid();

        var firstWrite = await first.Client.PostAsJsonAsync("/api/v1/memory/remember",
            new MemoryController.RememberRequest("Влад предпочитает зеленый чай", Category: "food", OperationId: operationId));
        firstWrite.EnsureSuccessStatusCode();
        var receipt = await firstWrite.Content.ReadFromJsonAsync<MemoryOperationResult>();
        Assert.NotNull(receipt);
        Assert.Equal("remembered", receipt!.Code);
        Assert.NotNull(receipt.MemoryItemId);

        var retry = await first.Client.PostAsJsonAsync("/api/v1/memory/remember",
            new MemoryController.RememberRequest("Влад предпочитает зеленый чай", Category: "food", OperationId: operationId));
        var retryReceipt = await retry.Content.ReadFromJsonAsync<MemoryOperationResult>();
        Assert.Equal("remembered", retryReceipt!.Code);

        var firstItems = await first.Client.GetFromJsonAsync<List<MemoryItemResponse>>("/api/v1/memory/items");
        var item = Assert.Single(firstItems!);
        Assert.Equal("Влад предпочитает зеленый чай", item.Text);
        Assert.Equal("food", item.Category);

        var foreignForget = await second.Client.PostAsJsonAsync("/api/v1/memory/forget",
            new MemoryController.ForgetRequest(item.Id, Guid.NewGuid()));
        var foreignReceipt = await foreignForget.Content.ReadFromJsonAsync<MemoryOperationResult>();
        Assert.Equal("not_found", foreignReceipt!.Code);
        Assert.Single((await first.Client.GetFromJsonAsync<List<MemoryItemResponse>>("/api/v1/memory/items"))!);
    }

    [Fact]
    public async Task ClearRequiresBoundConfirmationToken()
    {
        var user = await RegisterClientAsync("verified-memory-clear");
        await user.Client.PostAsJsonAsync("/api/v1/memory/remember",
            new MemoryController.RememberRequest("Пользователь любит прогулки", OperationId: Guid.NewGuid()));

        var prepare = await user.Client.PostAsJsonAsync("/api/v1/memory/clear/prepare",
            new MemoryController.PrepareClearRequest(Guid.NewGuid()));
        var prepared = await prepare.Content.ReadFromJsonAsync<MemoryOperationResult>();
        Assert.NotNull(prepared?.ConfirmationToken);

        var rejected = await user.Client.PostAsJsonAsync("/api/v1/memory/clear",
            new MemoryController.ClearRequest(prepared!.OperationId, "wrong-token"));
        var rejectedReceipt = await rejected.Content.ReadFromJsonAsync<MemoryOperationResult>();
        Assert.Equal("confirmation_invalid", rejectedReceipt!.Code);
        Assert.Single((await user.Client.GetFromJsonAsync<List<MemoryItemResponse>>("/api/v1/memory/items"))!);

        var clear = await user.Client.PostAsJsonAsync("/api/v1/memory/clear",
            new MemoryController.ClearRequest(prepared.OperationId, prepared.ConfirmationToken!));
        var clearReceipt = await clear.Content.ReadFromJsonAsync<MemoryOperationResult>();
        Assert.Equal("cleared", clearReceipt!.Code);
        Assert.Empty((await user.Client.GetFromJsonAsync<List<MemoryItemResponse>>("/api/v1/memory/items"))!);
    }

    private async Task<(HttpClient Client, string Email)> RegisterClientAsync(string prefix)
    {
        var client = _factory.CreateClient();
        var email = $"{prefix}-{Guid.NewGuid():N}@example.com";
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new AuthController.RegisterRequest(email, "correct-password-123", "ru"));
        register.EnsureSuccessStatusCode();
        var token = (await register.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, email);
    }

    private async Task<int> SeedFactAsync(string email, string fact)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var memory = new MemoryFact
        {
            UserId = user!.Id,
            Fact = fact,
            ContentHash = LongTermMemoryService.ComputeContentHash(fact),
            Embedding = new Vector(new float[GeminiService.EmbeddingDimensions]),
            EmbeddingModel = GeminiService.EmbeddingModel
        };
        db.MemoryFacts.Add(memory);
        await db.SaveChangesAsync();
        return memory.Id;
    }
}
