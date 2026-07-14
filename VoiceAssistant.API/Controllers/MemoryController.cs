using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VoiceAssistant.API.Data;
using VoiceAssistant.API.Services;

namespace VoiceAssistant.API.Controllers;

[ApiController]
[Route("api/v1/memory")]
[Authorize]
public class MemoryController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly MemoryItemService _memory;

    public MemoryController(AppDbContext db, MemoryItemService memory)
    {
        _db = db;
        _memory = memory;
    }

    public record MemoryFactResponse(int Id, string Fact, DateTime CreatedAt, DateTime UpdatedAt, DateTime? LastRecalledAt);
    public record RememberRequest(string Text, Guid? OperationId = null);
    public record CorrectRequest(Guid Id, string Text, Guid? OperationId = null);
    public record ForgetRequest(Guid Id, Guid? OperationId = null);
    public record PrepareClearRequest(Guid? OperationId = null);
    public record ClearRequest(Guid OperationId, string ConfirmationToken);

    [HttpGet("status")]
    public async Task<ActionResult<MemoryStatusResponse>> GetStatus(CancellationToken cancellationToken) =>
        Ok(await _memory.GetStatusAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!, cancellationToken));

    [HttpGet("items")]
    public async Task<ActionResult<IReadOnlyList<MemoryItemResponse>>> GetItems(
        [FromQuery] int limit = 100, CancellationToken cancellationToken = default) =>
        Ok(await _memory.ListAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!, limit, cancellationToken));

    [HttpGet("search")]
    public async Task<ActionResult<MemorySearchResponse>> Search([FromQuery] string query, CancellationToken cancellationToken) =>
        Ok(await _memory.SearchAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!, query, cancellationToken));

    [HttpPost("remember")]
    public async Task<ActionResult<MemoryOperationResult>> Remember([FromBody] RememberRequest request, CancellationToken cancellationToken) =>
        Ok(await _memory.RememberAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!, request.Text, null, request.OperationId, cancellationToken));

    [HttpPost("correct")]
    public async Task<ActionResult<MemoryOperationResult>> Correct([FromBody] CorrectRequest request, CancellationToken cancellationToken) =>
        Ok(await _memory.CorrectAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!, request.Id, request.Text, request.OperationId, cancellationToken));

    [HttpPost("forget")]
    public async Task<ActionResult<MemoryOperationResult>> Forget([FromBody] ForgetRequest request, CancellationToken cancellationToken) =>
        Ok(await _memory.ForgetAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!, request.Id, request.OperationId, cancellationToken));

    [HttpPost("clear/prepare")]
    public async Task<ActionResult<MemoryOperationResult>> PrepareClear([FromBody] PrepareClearRequest request, CancellationToken cancellationToken) =>
        Ok(await _memory.PrepareClearAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!, request.OperationId, cancellationToken));

    [HttpPost("clear")]
    public async Task<ActionResult<MemoryOperationResult>> Clear([FromBody] ClearRequest request, CancellationToken cancellationToken) =>
        Ok(await _memory.ClearAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!, request.OperationId, request.ConfirmationToken, cancellationToken));

    [HttpGet("facts")]
    public async Task<ActionResult<IReadOnlyList<MemoryFactResponse>>> GetFacts(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var facts = await _db.MemoryFacts
            .AsNoTracking()
            .Where(memory => memory.UserId == userId && memory.IsActive &&
                (!_db.MemoryItems.Any(item => item.LegacyMemoryFactId == memory.Id) ||
                 _db.MemoryItems.Any(item => item.LegacyMemoryFactId == memory.Id && item.Status == "active")))
            .OrderByDescending(memory => memory.UpdatedAt)
            .Select(memory => new MemoryFactResponse(
                memory.Id,
                memory.Fact,
                memory.CreatedAt,
                memory.UpdatedAt,
                memory.LastRecalledAt))
            .ToListAsync(cancellationToken);

        return Ok(facts);
    }

    [HttpDelete("facts/{id:int}")]
    public async Task<IActionResult> DeleteFact(int id, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var fact = await _db.MemoryFacts.SingleOrDefaultAsync(
            memory => memory.Id == id && memory.UserId == userId,
            cancellationToken);
        if (fact is null) return NotFound();

        // Legacy endpoint is a compatibility adapter. Keep the original row
        // for migration/audit provenance and tombstone its canonical twin.
        fact.IsActive = false;
        var canonical = await _db.MemoryItems.SingleOrDefaultAsync(
            item => item.LegacyMemoryFactId == fact.Id && item.UserId == userId,
            cancellationToken);
        if (canonical is not null && canonical.Status == "active")
        {
            canonical.Status = "tombstoned";
            canonical.TombstonedAt = DateTime.UtcNow;
            canonical.UpdatedAt = DateTime.UtcNow;
            canonical.Revision++;
        }
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpDelete("facts")]
    public IActionResult DeleteAllFacts()
    {
        // A mass tombstone must be bound to the explicit clear confirmation
        // token. Keeping the old DELETE route as a silent bypass would make
        // it impossible to uphold the verified lifecycle contract.
        return Conflict(new { error = "Use POST /api/v1/memory/clear/prepare followed by POST /api/v1/memory/clear." });
    }
}
