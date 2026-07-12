using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VoiceAssistant.API.Data;
using VoiceAssistant.API.Data.Entities;
using VoiceAssistant.API.Services;

namespace VoiceAssistant.API.Controllers;

[ApiController]
[Route("api/v1/admin")]
[Authorize(Roles = AdminBootstrapper.RoleName)]
public class AdminController : ControllerBase
{
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;

    private readonly AppDbContext _db;
    private readonly UserManager<User> _userManager;

    public AdminController(AppDbContext db, UserManager<User> userManager)
    {
        _db = db;
        _userManager = userManager;
    }

    public record OverviewResponse(
        int TotalUsers,
        int ApprovedUsers,
        int PendingUsers,
        int ActiveLast24Hours,
        int ActiveLast7Days,
        long TotalMessages,
        long TotalCharacters);

    public record UserRow(
        string Id,
        string Email,
        DateTime CreatedAt,
        DateTime LastActiveAt,
        bool IsApproved,
        bool IsAdmin,
        long MessageCount,
        long CharacterCount);

    public record PagedUsersResponse(
        IReadOnlyList<UserRow> Items,
        int Page,
        int PageSize,
        int TotalCount,
        int TotalPages);

    public record ApprovalRequest(bool IsApproved);

    [HttpGet("overview")]
    public async Task<ActionResult<OverviewResponse>> GetOverview(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        // One scoped DbContext cannot execute queries concurrently. Keep these
        // short aggregates sequential; parallel fan-out here would recreate the
        // exact EF Core race already fixed in the chat flow.
        var totalUsers = await _db.Users.CountAsync(cancellationToken);
        var approvedUsers = await _db.Users.CountAsync(u => u.IsApproved, cancellationToken);
        var active24 = await _db.Users.CountAsync(u => u.LastActiveAt >= now.AddHours(-24), cancellationToken);
        var active7 = await _db.Users.CountAsync(u => u.LastActiveAt >= now.AddDays(-7), cancellationToken);
        var totalMessages = await _db.Messages.LongCountAsync(cancellationToken);
        var totalCharacters = await _db.Messages.SumAsync(m => (long?)m.Content.Length, cancellationToken) ?? 0;

        return Ok(new OverviewResponse(
            totalUsers,
            approvedUsers,
            totalUsers - approvedUsers,
            active24,
            active7,
            totalMessages,
            totalCharacters));
    }

    [HttpGet("users")]
    public async Task<ActionResult<PagedUsersResponse>> GetUsers(
        [FromQuery] string? search,
        [FromQuery] string status = "all",
        [FromQuery] string sort = "lastActiveDesc",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = _db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalizedSearch = search.Trim().ToUpperInvariant();
            query = query.Where(u => u.NormalizedEmail != null && u.NormalizedEmail.Contains(normalizedSearch));
        }

        query = status.ToLowerInvariant() switch
        {
            "approved" => query.Where(u => u.IsApproved),
            "pending" => query.Where(u => !u.IsApproved),
            _ => query
        };

        query = sort switch
        {
            "createdAsc" => query.OrderBy(u => u.CreatedAt),
            "createdDesc" => query.OrderByDescending(u => u.CreatedAt),
            "emailAsc" => query.OrderBy(u => u.Email),
            "messagesDesc" => query.OrderByDescending(u => u.ChatSessions.SelectMany(s => s.Messages).Count()),
            _ => query.OrderByDescending(u => u.LastActiveAt)
        };

        var totalCount = await query.CountAsync(cancellationToken);
        var adminRoleId = await _db.Roles
            .Where(r => r.NormalizedName == AdminBootstrapper.RoleName.ToUpperInvariant())
            .Select(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var adminUserIds = adminRoleId is null
            ? new List<string>()
            : await _db.UserRoles
                .Where(ur => ur.RoleId == adminRoleId)
                .Select(ur => ur.UserId)
                .ToListAsync(cancellationToken);

        var users = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new UserRow(
                u.Id,
                u.Email ?? "",
                u.CreatedAt,
                u.LastActiveAt,
                u.IsApproved,
                adminUserIds.Contains(u.Id),
                u.ChatSessions.SelectMany(s => s.Messages).LongCount(),
                u.ChatSessions.SelectMany(s => s.Messages).Sum(m => (long?)m.Content.Length) ?? 0))
            .ToListAsync(cancellationToken);

        return Ok(new PagedUsersResponse(
            users,
            page,
            pageSize,
            totalCount,
            Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize))));
    }

    [HttpPatch("users/{id}/approval")]
    public async Task<ActionResult<UserRow>> SetApproval(
        string id,
        [FromBody] ApprovalRequest request,
        CancellationToken cancellationToken)
    {
        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (id == currentUserId && !request.IsApproved)
            return BadRequest(new { error = "Нельзя заблокировать собственную учетную запись администратора" });

        var user = await _userManager.FindByIdAsync(id);
        if (user is null) return NotFound();

        if (user.IsApproved != request.IsApproved)
        {
            user.IsApproved = request.IsApproved;
            var stampResult = await _userManager.UpdateSecurityStampAsync(user);
            if (!stampResult.Succeeded)
                return Problem("Не удалось обновить статус пользователя");
        }

        var isAdmin = await _userManager.IsInRoleAsync(user, AdminBootstrapper.RoleName);
        var usage = await _db.Users
            .Where(u => u.Id == id)
            .Select(u => new
            {
                MessageCount = u.ChatSessions.SelectMany(s => s.Messages).LongCount(),
                CharacterCount = u.ChatSessions.SelectMany(s => s.Messages).Sum(m => (long?)m.Content.Length) ?? 0
            })
            .SingleAsync(cancellationToken);

        return Ok(new UserRow(
            user.Id,
            user.Email ?? "",
            user.CreatedAt,
            user.LastActiveAt,
            user.IsApproved,
            isAdmin,
            usage.MessageCount,
            usage.CharacterCount));
    }
}
