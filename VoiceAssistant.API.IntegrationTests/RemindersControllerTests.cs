using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VoiceAssistant.API.Controllers;
using VoiceAssistant.API.Data;
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
            $"/api/v1/reminders?deviceId={DeviceId}&protocolVersion=2");
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
    public async Task CancelledReminder_WithUnacknowledgedDelivery_IsReturnedForDeviceCleanup()
    {
        var client = await RegisterClientAsync("reminder-cancel-before-ack");
        var reminderId = await CreateReminderAndReadEventAsync(client);

        using (var cancel = await client.DeleteAsync($"/api/v1/reminders/{reminderId}"))
            cancel.EnsureSuccessStatusCode();

        // A local alarm may already exist even if the scheduled acknowledgement
        // was lost before it reached the server. Return this tombstone until the
        // originating device acknowledges its local cleanup.
        var tombstones = await client.GetFromJsonAsync<List<RemindersController.ReminderResponse>>(
            $"/api/v1/reminders?deviceId={DeviceId}");
        var tombstone = Assert.Single(tombstones!);
        Assert.Equal(ReminderStatuses.Cancelled, tombstone.Status);
        Assert.Equal(ReminderDeliveryStatuses.Pending, tombstone.DeliveryStatus);

        var cleanupAck = await client.PostAsJsonAsync(
            $"/api/v1/reminders/{reminderId}/cancelled",
            new RemindersController.DeliveryAckRequest(DeviceId, null, null));
        Assert.Equal(HttpStatusCode.NoContent, cleanupAck.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<RemindersController.ReminderResponse>>(
            $"/api/v1/reminders?deviceId={DeviceId}"))!);
    }

    [Fact]
    public async Task ChatPeriodicReminderEvent_ProtocolV2_DeviceAckPersistsRecurrence()
    {
        var client = await RegisterClientAsync("periodic-reminder-owner");
        var session = (await client.GetFromJsonAsync<List<ChatSession>>("/api/v1/chat/sessions"))!.Single();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/chat/send")
        {
            Content = JsonContent.Create(new ChatController.SendRequest(
                session.Id,
                "Напоминай каждый день принимать витамин D",
                DeviceId: DeviceId,
                TimeZoneId: "UTC",
                ReminderProtocolVersion: 2))
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
            if (!json.RootElement.TryGetProperty("periodicReminder", out var reminder)) continue;
            Assert.Equal(2, reminder.GetProperty("contractVersion").GetInt32());
            Assert.Equal("FREQ=DAILY", reminder.GetProperty("rrule").GetString());
            reminderId = reminder.GetProperty("id").GetInt32();
            var ack = await client.PostAsJsonAsync(
                $"/api/v1/reminders/{reminderId}/scheduled",
                new RemindersController.DeliveryAckRequest(DeviceId, "local-periodic-notification-123", null));
            Assert.Equal(HttpStatusCode.NoContent, ack.StatusCode);
        }

        Assert.True(sawDone);
        Assert.NotNull(reminderId);
        var reminders = await client.GetFromJsonAsync<List<RemindersController.ReminderResponse>>(
            $"/api/v1/reminders?deviceId={DeviceId}&protocolVersion=2");
        var saved = Assert.Single(reminders!);
        Assert.Equal(reminderId, saved.Id);
        Assert.Equal("FREQ=DAILY", saved.RecurrenceRule);
        Assert.Equal("scheduled", saved.DeliveryStatus);
        Assert.Equal("local-periodic-notification-123", saved.LocalNotificationId);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var entity = await db.Reminders.SingleAsync(item => item.Id == reminderId);
            entity.DueAtUtc = DateTime.UtcNow.AddDays(-1);
            await db.SaveChangesAsync();
        }

        var afterFirstOccurrence = await client.GetFromJsonAsync<List<RemindersController.ReminderResponse>>(
            $"/api/v1/reminders?deviceId={DeviceId}&protocolVersion=2");
        Assert.Equal("FREQ=DAILY", Assert.Single(afterFirstOccurrence!).RecurrenceRule);
        Assert.Empty((await client.GetFromJsonAsync<List<RemindersController.ReminderResponse>>(
            $"/api/v1/reminders?deviceId={DeviceId}"))!);

        var suspendOnDevice = await client.PostAsJsonAsync(
            $"/api/v1/reminders/{reminderId}/cancelled",
            new RemindersController.DeliveryAckRequest(DeviceId, null, null));
        Assert.Equal(HttpStatusCode.NoContent, suspendOnDevice.StatusCode);
        var resumableOnSameDevice = Assert.Single(
            (await client.GetFromJsonAsync<List<RemindersController.ReminderResponse>>(
                $"/api/v1/reminders?deviceId={DeviceId}&protocolVersion=2"))!);
        Assert.Equal(ReminderDeliveryStatuses.Cancelled, resumableOnSameDevice.DeliveryStatus);
        Assert.Equal("FREQ=DAILY", Assert.Single(
            (await client.GetFromJsonAsync<List<RemindersController.ReminderResponse>>(
                "/api/v1/reminders?deviceId=device-integration-456&protocolVersion=2"))!).RecurrenceRule);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var entity = await db.Reminders.SingleAsync(item => item.Id == reminderId);
            entity.RecurrenceRule = "FREQ=HOURLY;INTERVAL=2";
            await db.SaveChangesAsync();
        }
        Assert.Empty((await client.GetFromJsonAsync<List<RemindersController.ReminderResponse>>(
            $"/api/v1/reminders?deviceId={DeviceId}&protocolVersion=2"))!);
    }

    [Fact]
    public async Task ChatPeriodicReminder_ProtocolV1DoesNotEmitOrPersistSchedule()
    {
        var client = await RegisterClientAsync("periodic-reminder-v1-client");
        var session = (await client.GetFromJsonAsync<List<ChatSession>>("/api/v1/chat/sessions"))!.Single();
        using var response = await client.PostAsJsonAsync(
            "/api/v1/chat/send",
            new ChatController.SendRequest(
                session.Id,
                "Напоминай каждый день принимать витамин D",
                DeviceId: DeviceId,
                TimeZoneId: "UTC"));

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("periodicReminder", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Передала периодическое напоминание", body, StringComparison.Ordinal);
        Assert.Empty((await client.GetFromJsonAsync<List<RemindersController.ReminderResponse>>(
            $"/api/v1/reminders?deviceId={DeviceId}&protocolVersion=2"))!);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userId = await db.ChatSessions
            .Where(item => item.Id == session.Id)
            .Select(item => item.UserId)
            .SingleAsync();
        Assert.False(await db.Reminders.AnyAsync(item => item.UserId == userId));
    }

    [Fact]
    public async Task ChatPeriodicReminder_InvalidRuleCannotDowngradeToOneShotInSameTurn()
    {
        var client = await RegisterClientAsync("periodic-reminder-no-downgrade");
        var session = (await client.GetFromJsonAsync<List<ChatSession>>("/api/v1/chat/sessions"))!.Single();
        using var response = await client.PostAsJsonAsync(
            "/api/v1/chat/send",
            new ChatController.SendRequest(
                session.Id,
                "Создай периодическое напоминание без downgrade",
                DeviceId: DeviceId,
                TimeZoneId: "UTC",
                ReminderProtocolVersion: 2));

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("periodicReminder", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"reminder\":", body, StringComparison.Ordinal);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userId = await db.ChatSessions
            .Where(item => item.Id == session.Id)
            .Select(item => item.UserId)
            .SingleAsync();
        Assert.False(await db.Reminders.AnyAsync(item => item.UserId == userId));
    }

    [Fact]
    public async Task ChatPeriodicReminder_RetriedClientTurn_ReusesSchedule()
    {
        var client = await RegisterClientAsync("periodic-reminder-retry");
        var session = (await client.GetFromJsonAsync<List<ChatSession>>("/api/v1/chat/sessions"))!.Single();
        var clientTurnId = Guid.NewGuid();

        var firstId = await CreatePeriodicReminderAndAckAsync(
            client, session.Id, clientTurnId, "Напоминай каждый день принимать витамин D");
        // A transport retry runs the model again. Even if its proposal drifts
        // to another reminder tool/arguments, the stable client-turn slot must
        // return the already-created schedule instead of creating a duplicate.
        var secondId = await CreatePeriodicReminderAndAckAsync(
            client, session.Id, clientTurnId, "Напомни завтра позвонить врачу");

        Assert.Equal(firstId, secondId);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userId = await db.ChatSessions
            .Where(item => item.Id == session.Id)
            .Select(item => item.UserId)
            .SingleAsync();
        var reminder = Assert.Single(await db.Reminders.Where(item => item.UserId == userId).ToListAsync());
        Assert.Equal(firstId, reminder.Id);
        Assert.Equal("FREQ=DAILY", reminder.RecurrenceRule);
    }

    [Fact]
    public async Task ChatPeriodicReminder_RetryCannotResurrectCancelledSchedule()
    {
        var client = await RegisterClientAsync("periodic-reminder-cancelled-retry");
        var session = (await client.GetFromJsonAsync<List<ChatSession>>("/api/v1/chat/sessions"))!.Single();
        var clientTurnId = Guid.NewGuid();
        var reminderId = await CreatePeriodicReminderAndAckAsync(
            client, session.Id, clientTurnId, "Напоминай каждый день принимать витамин D");

        using (var cancel = await client.DeleteAsync($"/api/v1/reminders/{reminderId}"))
            cancel.EnsureSuccessStatusCode();

        using var retry = await client.PostAsJsonAsync(
            "/api/v1/chat/send",
            new ChatController.SendRequest(
                session.Id,
                "Напоминай каждый день принимать витамин D",
                DeviceId: DeviceId,
                TimeZoneId: "UTC",
                ReminderProtocolVersion: 2,
                ClientTurnId: clientTurnId));
        retry.EnsureSuccessStatusCode();
        var body = await retry.Content.ReadAsStringAsync();
        Assert.DoesNotContain("periodicReminder", body, StringComparison.Ordinal);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reminder = await db.Reminders.SingleAsync(item => item.Id == reminderId);
        Assert.Equal(ReminderStatuses.Cancelled, reminder.Status);
    }

    [Fact]
    public async Task ChatPeriodicReminder_SameTurnRetryCannotReactivateSuspendedDeviceDelivery()
    {
        var client = await RegisterClientAsync("periodic-reminder-suspended-retry");
        var session = (await client.GetFromJsonAsync<List<ChatSession>>("/api/v1/chat/sessions"))!.Single();
        var clientTurnId = Guid.NewGuid();
        var reminderId = await CreatePeriodicReminderAndAckAsync(
            client, session.Id, clientTurnId, "Напоминай каждый день принимать витамин D");
        using (var suspend = await client.PostAsJsonAsync(
                   $"/api/v1/reminders/{reminderId}/cancelled",
                   new RemindersController.DeliveryAckRequest(DeviceId, null, null)))
            suspend.EnsureSuccessStatusCode();

        using var retry = await client.PostAsJsonAsync(
            "/api/v1/chat/send",
            new ChatController.SendRequest(
                session.Id,
                "Напоминай каждый день принимать витамин D",
                DeviceId: DeviceId,
                TimeZoneId: "UTC",
                ReminderProtocolVersion: 2,
                ClientTurnId: clientTurnId));
        retry.EnsureSuccessStatusCode();
        Assert.DoesNotContain("periodicReminder", await retry.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var delivery = await db.ReminderDeliveries.SingleAsync(item =>
            item.ReminderId == reminderId && item.DeviceId == DeviceId);
        Assert.Equal(ReminderDeliveryStatuses.Cancelled, delivery.Status);
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

    private static async Task<int> CreatePeriodicReminderAndAckAsync(
        HttpClient client,
        int sessionId,
        Guid clientTurnId,
        string message)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/chat/send")
        {
            Content = JsonContent.Create(new ChatController.SendRequest(
                sessionId,
                message,
                DeviceId: DeviceId,
                TimeZoneId: "UTC",
                ReminderProtocolVersion: 2,
                ClientTurnId: clientTurnId))
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        int? reminderId = null;

        while (await reader.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith("data: ")) continue;
            var data = line[6..].Trim();
            if (data == "[DONE]") break;
            using var json = JsonDocument.Parse(data);
            if (!json.RootElement.TryGetProperty("periodicReminder", out var reminder)) continue;
            reminderId = reminder.GetProperty("id").GetInt32();
            var ack = await client.PostAsJsonAsync(
                $"/api/v1/reminders/{reminderId}/scheduled",
                new RemindersController.DeliveryAckRequest(DeviceId, "local-periodic-retry", null));
            Assert.Equal(HttpStatusCode.NoContent, ack.StatusCode);
        }

        return reminderId ?? throw new Xunit.Sdk.XunitException("Periodic reminder event was not emitted");
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
