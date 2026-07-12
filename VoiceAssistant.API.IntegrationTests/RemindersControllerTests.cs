using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using VoiceAssistant.API.Controllers;
using VoiceAssistant.API.Data.Entities;
using Xunit;

namespace VoiceAssistant.API.IntegrationTests;

public class RemindersControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private const string DeviceId = "device-integration-123";
    private readonly TestWebApplicationFactory _factory;

    public RemindersControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ChatReminderEvent_DeviceAck_MarksLocalDeliveryScheduled()
    {
        var client = await RegisterClientAsync("reminder-owner");
        var session = (await client.GetFromJsonAsync<List<ChatSession>>("/api/v1/chat/sessions"))!.Single();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/chat/send")
        {
            Content = JsonContent.Create(new ChatController.SendRequest(
                session.Id,
                "Напомни завтра позвонить врачу",
                DeviceId: DeviceId,
                TimeZoneId: "UTC"))
        };

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        int? reminderId = null;
        var sawDone = false;

        while (await reader.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith("data: ")) continue;
            var data = line[6..].Trim();
            if (data == "[DONE]")
            {
                sawDone = true;
                break;
            }

            using var json = JsonDocument.Parse(data);
            if (!json.RootElement.TryGetProperty("reminder", out var reminder)) continue;
            reminderId = reminder.GetProperty("id").GetInt32();
            var ack = await client.PostAsJsonAsync(
                $"/api/v1/reminders/{reminderId}/scheduled",
                new RemindersController.DeliveryAckRequest(DeviceId, "local-notification-123", null));
            Assert.Equal(HttpStatusCode.NoContent, ack.StatusCode);
        }

        Assert.True(sawDone);
        Assert.NotNull(reminderId);
        var reminders = await client.GetFromJsonAsync<List<RemindersController.ReminderResponse>>(
            $"/api/v1/reminders?deviceId={DeviceId}");
        var saved = Assert.Single(reminders!);
        Assert.Equal(reminderId, saved.Id);
        Assert.Equal("scheduled", saved.DeliveryStatus);
        Assert.Equal("local-notification-123", saved.LocalNotificationId);

        var cancel = await client.DeleteAsync($"/api/v1/reminders/{reminderId}");
        cancel.EnsureSuccessStatusCode();
        var tombstones = await client.GetFromJsonAsync<List<RemindersController.ReminderResponse>>(
            $"/api/v1/reminders?deviceId={DeviceId}");
        Assert.Equal("cancelled", Assert.Single(tombstones!).Status);

        var cancelAck = await client.PostAsJsonAsync(
            $"/api/v1/reminders/{reminderId}/cancelled",
            new RemindersController.DeliveryAckRequest(DeviceId, null, null));
        Assert.Equal(HttpStatusCode.NoContent, cancelAck.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<RemindersController.ReminderResponse>>(
            $"/api/v1/reminders?deviceId={DeviceId}"))!);
    }

    [Fact]
    public async Task DeliveryAck_FromAnotherUser_ReturnsNotFound()
    {
        var owner = await RegisterClientAsync("reminder-owner-isolation");
        var other = await RegisterClientAsync("reminder-other-isolation");
        var reminderId = await CreateReminderAndReadEventAsync(owner);

        var response = await other.PostAsJsonAsync(
            $"/api/v1/reminders/{reminderId}/scheduled",
            new RemindersController.DeliveryAckRequest(DeviceId, "foreign-local-id", null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<int> CreateReminderAndReadEventAsync(HttpClient client)
    {
        var session = (await client.GetFromJsonAsync<List<ChatSession>>("/api/v1/chat/sessions"))!.Single();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/chat/send")
        {
            Content = JsonContent.Create(new ChatController.SendRequest(
                session.Id,
                "Напомни завтра позвонить врачу",
                DeviceId: DeviceId,
                TimeZoneId: "UTC"))
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith("data: ")) continue;
            var data = line[6..].Trim();
            if (data == "[DONE]") break;
            using var json = JsonDocument.Parse(data);
            if (json.RootElement.TryGetProperty("reminder", out var reminder))
                return reminder.GetProperty("id").GetInt32();
        }
        throw new Xunit.Sdk.XunitException("Reminder event was not emitted");
    }

    private async Task<HttpClient> RegisterClientAsync(string prefix)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new AuthController.RegisterRequest($"{prefix}-{Guid.NewGuid():N}@example.com", "correct-password-123", "ru"));
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
