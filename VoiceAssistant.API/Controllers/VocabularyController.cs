using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VoiceAssistant.API.Data;
using VoiceAssistant.API.Data.Entities;
using VoiceAssistant.API.Services;

namespace VoiceAssistant.API.Controllers;

[ApiController]
[Route("api/vocabulary")]
[Authorize]
public class VocabularyController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AnthropicService _anthropic;

    public VocabularyController(AppDbContext db, AnthropicService anthropic)
    {
        _db = db;
        _anthropic = anthropic;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [HttpGet]
    public async Task<IActionResult> GetWords([FromQuery] string? status, [FromQuery] string? search,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var query = _db.UserWords.Where(w => w.UserId == UserId);

        if (!string.IsNullOrEmpty(status) && status != "all")
            query = query.Where(w => w.Status == status);

        if (!string.IsNullOrEmpty(search))
            query = query.Where(w => w.Word.Contains(search) || w.Translation.Contains(search));

        var total = await query.CountAsync();
        var words = await query
            .OrderByDescending(w => w.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(w => new { w.Id, w.Word, w.Translation, w.GrammarInfo, w.Status, w.ErrorCount, w.CreatedAt })
            .ToListAsync();

        return Ok(new { total, words });
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        var words = _db.UserWords.Where(w => w.UserId == UserId);
        return Ok(new
        {
            total = await words.CountAsync(),
            @new = await words.CountAsync(w => w.Status == "new"),
            learning = await words.CountAsync(w => w.Status == "learning"),
            known = await words.CountAsync(w => w.Status == "known")
        });
    }

    public record AddWordRequest(string Word, string Translation, string? GrammarInfo = null);

    [HttpPost("add")]
    public async Task<IActionResult> AddWord([FromBody] AddWordRequest req)
    {
        var exists = await _db.UserWords.AnyAsync(w =>
            w.UserId == UserId && w.Word.ToLower() == req.Word.ToLower());
        if (exists)
            return Conflict(new { error = "Word already in vocabulary" });

        var word = new UserWord
        {
            UserId = UserId,
            Word = req.Word.Trim(),
            Translation = req.Translation.Trim(),
            GrammarInfo = req.GrammarInfo
        };
        _db.UserWords.Add(word);
        await _db.SaveChangesAsync();

        return Ok(new { word.Id, word.Word, word.Translation, word.GrammarInfo, word.Status });
    }

    public record AnalyzeRequest(string Word);

    [HttpPost("analyze")]
    public async Task<IActionResult> Analyze([FromBody] AnalyzeRequest req)
    {
        var word = req.Word.Trim().ToLower();
        if (string.IsNullOrEmpty(word))
            return BadRequest(new { error = "Word is required" });

        var systemPrompt = @"Ти — словник польської мови. Для заданого слова поверни JSON (без markdown, тільки чистий JSON):
{
  ""word"": ""базова форма слова"",
  ""translation"": ""переклад українською"",
  ""partOfSpeech"": ""rzeczownik|czasownik|przymiotnik|przysłówek|inne"",
  ""grammar"": { ... }
}

Для rzeczownik (іменника) grammar:
{""rodzaj"":""m/f/n"",""przypadki"":{""mianownik"":""..."",""dopełniacz"":""..."",""celownik"":""..."",""biernik"":""..."",""narzędnik"":""..."",""miejscownik"":""..."",""wołacz"":""...""}}

Для czasownik (дієслова) grammar:
{""aspekt"":""dk/ndk"",""czas_teraźniejszy"":{""ja"":""..."",""ty"":""..."",""on/ona"":""..."",""my"":""..."",""wy"":""..."",""oni/one"":""...""},""czas_przeszły"":{""on"":""..."",""ona"":""..."",""oni"":""...""},""tryb_rozkazujący"":""...""}

Для przymiotnik (прикметника) grammar:
{""stopniowanie"":{""równy"":""..."",""wyższy"":""..."",""najwyższy"":""...""}}

Для інших частин мови — порожній об'єкт grammar: {}";

        var result = await _anthropic.CreateAsync(systemPrompt, word);
        // Strip markdown code fences if present
        var json = result.Trim();
        if (json.StartsWith("```"))
        {
            json = json[json.IndexOf('\n')..]; // remove first line (```json)
            json = json[..json.LastIndexOf("```")].Trim(); // remove closing ```
        }
        // Check if base form already exists in user's vocabulary
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var baseForm = doc.RootElement.TryGetProperty("word", out var wProp) ? wProp.GetString() : word;
        var alreadyExists = await _db.UserWords.AnyAsync(w =>
            w.UserId == UserId && w.Word.ToLower() == (baseForm ?? word).ToLower());

        if (alreadyExists)
        {
            // Insert flag before closing brace
            var flagged = json.TrimEnd()[..^1] + ",\"alreadyExists\":true}";
            return Content(flagged, "application/json");
        }

        return Content(json, "application/json");
    }

    public record UpdateStatusRequest(string Status);

    [HttpPut("{id:int}/status")]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] UpdateStatusRequest req)
    {
        var validStatuses = new[] { "new", "learning", "known" };
        if (!validStatuses.Contains(req.Status))
            return BadRequest(new { error = "Invalid status" });

        var word = await _db.UserWords.FirstOrDefaultAsync(w => w.Id == id && w.UserId == UserId);
        if (word == null) return NotFound();

        word.Status = req.Status;
        word.LastSeenAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { word.Id, word.Status });
    }
}
