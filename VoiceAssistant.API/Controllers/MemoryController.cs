using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VoiceAssistant.API.Data;

namespace VoiceAssistant.API.Controllers;

[ApiController]
[Route("api/v1/memory")]
[Authorize]
public class MemoryController : ControllerBase
{
    private readonly AppDbContext _db;

    public MemoryController(AppDbContext db)
    {
        _db = db;
    }

    public record MemoryFactResponse(int Id, string Fact, DateTime CreatedAt, DateTime UpdatedAt, DateTime? LastRecalledAt);

    [HttpGet("facts")]
    public async Task<ActionResult<IReadOnlyList<MemoryFactResponse>>> GetFacts(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var facts = await _db.MemoryFacts
            .AsNoTracking()
            .Where(memory => memory.UserId == userId && memory.IsActive)
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

        _db.MemoryFacts.Remove(fact);
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpDelete("facts")]
    public async Task<IActionResult> DeleteAllFacts(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await _db.MemoryFacts
            .Where(memory => memory.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);
        return NoContent();
    }
}
