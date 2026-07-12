using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VoiceAssistant.API.Data;
using VoiceAssistant.API.Data.Entities;
using VoiceAssistant.API.Services;

namespace VoiceAssistant.API.Controllers;

[ApiController]
[Route("api/v1/reminders")]
[Authorize]
public class RemindersController : ControllerBase
{
    private readonly AppDbContext _db;

    public RemindersController(AppDbContext db)
    {
        _db = db;
    }

    public record DeliveryAckRequest(string DeviceId, string? LocalNotificationId, string? Error);
    public record ReminderResponse(
        int Id,
        string Text,
        DateTime DueAtUtc,
        string TimeZoneId,
        string Status,
        string? DeliveryStatus,
        string? LocalNotificationId);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ReminderResponse>>> GetForDevice(
        [FromQuery] string deviceId,
        CancellationToken cancellationToken)
    {
        if (!ReminderService.IsValidDeviceId(deviceId))
            return BadRequest(new { error = "Invalid device id" });

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var reminders = await _db.Reminders
            .AsNoTracking()
            .Where(reminder => reminder.UserId == userId &&
                               ((reminder.Status == ReminderStatuses.Active &&
                                 reminder.DueAtUtc > DateTime.UtcNow.AddMinutes(-5)) ||
                                (reminder.Status == ReminderStatuses.Cancelled &&
                                 reminder.Deliveries.Any(delivery =>
                                     delivery.DeviceId == deviceId &&
                                     delivery.Status == ReminderDeliveryStatuses.Scheduled))))
            .OrderBy(reminder => reminder.DueAtUtc)
            .Select(reminder => new ReminderResponse(
                reminder.Id,
                reminder.Text,
                reminder.DueAtUtc,
                reminder.TimeZoneId,
                reminder.Status,
                reminder.Deliveries.Where(delivery => delivery.DeviceId == deviceId)
                    .Select(delivery => delivery.Status).FirstOrDefault(),
                reminder.Deliveries.Where(delivery => delivery.DeviceId == deviceId)
                    .Select(delivery => delivery.LocalNotificationId).FirstOrDefault()))
            .ToListAsync(cancellationToken);

        return Ok(reminders);
    }

    [HttpPost("{id:int}/scheduled")]
    public Task<IActionResult> MarkScheduled(int id, [FromBody] DeliveryAckRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.LocalNotificationId) || request.LocalNotificationId.Length > 200)
            return Task.FromResult<IActionResult>(BadRequest(new { error = "Local notification id is required" }));
        return UpdateDeliveryAsync(id, request, ReminderDeliveryStatuses.Scheduled, cancellationToken);
    }

    [HttpPost("{id:int}/failed")]
    public Task<IActionResult> MarkFailed(int id, [FromBody] DeliveryAckRequest request, CancellationToken cancellationToken) =>
        UpdateDeliveryAsync(id, request, ReminderDeliveryStatuses.Failed, cancellationToken);

    [HttpPost("{id:int}/cancelled")]
    public Task<IActionResult> MarkCancelled(int id, [FromBody] DeliveryAckRequest request, CancellationToken cancellationToken) =>
        UpdateDeliveryAsync(id, request, ReminderDeliveryStatuses.Cancelled, cancellationToken, allowCancelledReminder: true);

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Cancel(int id, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var reminder = await _db.Reminders
            .Include(item => item.Deliveries)
            .SingleOrDefaultAsync(item => item.Id == id && item.UserId == userId, cancellationToken);
        if (reminder is null) return NotFound();

        reminder.Status = ReminderStatuses.Cancelled;
        reminder.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(reminder.Deliveries
            .Where(delivery => !string.IsNullOrWhiteSpace(delivery.LocalNotificationId))
            .Select(delivery => new { delivery.DeviceId, delivery.LocalNotificationId }));
    }

    private async Task<IActionResult> UpdateDeliveryAsync(
        int reminderId,
        DeliveryAckRequest request,
        string status,
        CancellationToken cancellationToken,
        bool allowCancelledReminder = false)
    {
        if (!ReminderService.IsValidDeviceId(request.DeviceId))
            return BadRequest(new { error = "Invalid device id" });

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var reminder = await _db.Reminders
            .Include(item => item.Deliveries)
            .SingleOrDefaultAsync(item => item.Id == reminderId && item.UserId == userId, cancellationToken);
        if (reminder is null) return NotFound();
        if (reminder.Status != ReminderStatuses.Active && !(allowCancelledReminder && reminder.Status == ReminderStatuses.Cancelled))
            return Conflict(new { error = "Reminder is no longer active" });

        var delivery = reminder.Deliveries.SingleOrDefault(item => item.DeviceId == request.DeviceId);
        if (delivery is null)
        {
            delivery = new ReminderDelivery { DeviceId = request.DeviceId };
            reminder.Deliveries.Add(delivery);
        }

        delivery.Status = status;
        delivery.LocalNotificationId = status == ReminderDeliveryStatuses.Scheduled
            ? request.LocalNotificationId
            : null;
        delivery.Error = status == ReminderDeliveryStatuses.Failed
            ? Truncate(request.Error, 500)
            : null;
        delivery.ScheduledAtUtc = status == ReminderDeliveryStatuses.Scheduled ? DateTime.UtcNow : null;
        delivery.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= maxLength ? value : value[..maxLength];
}
