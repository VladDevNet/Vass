using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VoiceAssistant.API.Data;
using VoiceAssistant.API.Data.Entities;

namespace VoiceAssistant.API.Controllers;

// Ingest for mobile/src/logging/remoteLogger.ts — the client buffers log
// lines and POSTs them in batches, fire-and-forget (it doesn't await or
// react to the response), so this endpoint just needs to accept and store
// quickly, not validate strictly. Development-stage tooling for debugging
// the voice loop against a real device — see docs/react-native/BACKLOG.md.
[ApiController]
[Route("api/v1/client-logs")]
[Authorize]
public class ClientLogsController : ControllerBase
{
    private readonly AppDbContext _db;

    public ClientLogsController(AppDbContext db)
    {
        _db = db;
    }

    public record LogEntryDto(
        DateTime ClientTimestamp,
        string RunId,
        string Level,
        string Category,
        string Message,
        string? Data);

    public record BatchRequest(List<LogEntryDto> Entries);

    // Capped defensively — a runaway client loop shouldn't be able to push
    // an unbounded write in a single request; the client's own buffer flush
    // threshold (see remoteLogger.ts) stays well under this in practice.
    private const int MaxEntriesPerBatch = 500;

    [HttpPost("batch")]
    public async Task<IActionResult> PostBatch([FromBody] BatchRequest req)
    {
        if (req.Entries.Count == 0) return NoContent();

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var receivedAt = DateTime.UtcNow;

        foreach (var entry in req.Entries.Take(MaxEntriesPerBatch))
        {
            _db.ClientLogEntries.Add(new ClientLogEntry
            {
                UserId = userId,
                CreatedAt = receivedAt,
                ClientTimestamp = entry.ClientTimestamp,
                RunId = entry.RunId,
                Level = entry.Level,
                Category = entry.Category,
                Message = entry.Message,
                DataJson = entry.Data,
            });
        }

        await _db.SaveChangesAsync();
        return NoContent();
    }
}
