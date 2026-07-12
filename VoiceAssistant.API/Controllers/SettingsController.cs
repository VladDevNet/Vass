using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VoiceAssistant.API.Data;
using VoiceAssistant.API.Data.Entities;
using VoiceAssistant.API.Services;

namespace VoiceAssistant.API.Controllers;

[ApiController]
[Route("api/v1/settings")]
[Authorize]
public partial class SettingsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly CompanionPromptService _tutor;

    // Mirrors mobile/src/components/LayeredAvatar.tsx's AvatarId union
    // ('olga' | 'male') — the client already falls back to 'olga' for any
    // other value, so null (unset) is allowed but a garbage string is not
    // (PROJECT-AUDIT-2026-07-10 SEC-07).
    private static readonly HashSet<string> ValidAvatarIds = ["olga", "male"];

    // Structural check only (2-letter code, optional region subtag) — this
    // field isn't read anywhere server-side today (it's a holdover from the
    // pre-mobile-pivot web UI), so there's no real "supported languages"
    // list to validate against yet, just garbage/oversized input to reject.
    // \A/\z (not ^/$) — $ alone still matches immediately before a single
    // trailing newline, which would let e.g. "en-US\n" (6 chars) through
    // this check only to violate InterfaceLanguage's own HasMaxLength(5) on
    // save (same gotcha as ChatController's SafeAudioFileNamePattern).
    [GeneratedRegex(@"\A[a-z]{2}(-[A-Z]{2})?\z")]
    private static partial Regex InterfaceLanguagePattern();

    // Reinjected into every future Gemini system prompt (see
    // CompanionPromptService.BuildSystemPrompt) — generous enough for
    // legitimate accumulated instructions, bounded against someone pasting
    // an arbitrarily large blob that inflates the cost of every future turn.
    private const int MaxCustomSystemPromptLength = 4000;

    public SettingsController(AppDbContext db, CompanionPromptService tutor)
    {
        _db = db;
        _tutor = tutor;
    }

    public record SettingsResponse(
        string? DisplayName,
        string? AssistantName,
        string? AvatarId,
        string InterfaceLanguage,
        string? OpenAiApiKey,
        string? AnthropicApiKey,
        string? GeminiApiKey,
        string? CustomSystemPrompt,
        bool FullTranslation);

    public record SettingsUpdateRequest(
        string? DisplayName,
        string? AssistantName,
        string? AvatarId,
        string? InterfaceLanguage,
        string? OpenAiApiKey,
        string? AnthropicApiKey,
        string? GeminiApiKey,
        string? CustomSystemPrompt,
        bool? FullTranslation);

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var settings = await _db.UserSettings.FirstOrDefaultAsync(s => s.UserId == userId);

        return Ok(new SettingsResponse(
            settings?.DisplayName,
            settings?.AssistantName,
            settings?.AvatarId,
            settings?.InterfaceLanguage ?? "uk",
            MaskKey(settings?.OpenAiApiKey),
            MaskKey(settings?.AnthropicApiKey),
            MaskKey(settings?.GeminiApiKey),
            settings?.CustomSystemPrompt,
            settings?.FullTranslation ?? false
        ));
    }

    // PATCH, not PUT: every field is optional and independently applied only
    // if the caller actually sent it -- omitted/null fields are left exactly
    // as they are server-side (PROJECT-AUDIT-2026-07-10 API-01). This is what
    // actually closes the lost-update risk the audit describes (mobile used
    // to GET the whole object, then PUT the whole object back to change one
    // field, silently clobbering anything changed concurrently -- e.g. by
    // ChatController's own "запомни, говори медленнее" voice-command write to
    // CustomSystemPrompt racing a mobile-initiated name change). Two callers
    // PATCHing DIFFERENT fields can no longer clobber each other, since
    // neither request touches the other's field at all.
    //
    // For the string fields below (DisplayName/AssistantName/
    // CustomSystemPrompt), an EMPTY string is the explicit "clear this field"
    // signal, distinct from null/omitted ("don't touch") -- same convention
    // the API-key fields already used before this change. AvatarId/
    // InterfaceLanguage don't support an explicit-clear signal: nothing
    // needs to un-set them today, and doing so would mean special-casing
    // "" past their allowlist/regex validation for no real benefit.
    [HttpPatch]
    public async Task<IActionResult> Patch([FromBody] SettingsUpdateRequest req)
    {
        // DisplayName/AssistantName are HasMaxLength(100) at the DB level —
        // without this check, exceeding it surfaces as a raw 500 from a
        // Postgres string-truncation error instead of a clean validation
        // response. Found by testing a 101-char value against an isolated
        // build before merging, not by inspection alone.
        if (req.DisplayName?.Length > 100)
            return BadRequest(new { error = "Имя слишком длинное (максимум 100 символов)" });
        if (req.AssistantName?.Length > 100)
            return BadRequest(new { error = "Имя ассистента слишком длинное (максимум 100 символов)" });
        if (req.AvatarId is not null && !ValidAvatarIds.Contains(req.AvatarId))
            return BadRequest(new { error = "Недопустимый AvatarId" });
        if (req.InterfaceLanguage is not null && !InterfaceLanguagePattern().IsMatch(req.InterfaceLanguage))
            return BadRequest(new { error = "Недопустимый код языка" });
        if (req.CustomSystemPrompt?.Length > MaxCustomSystemPromptLength)
            return BadRequest(new { error = $"Инструкции слишком длинные (максимум {MaxCustomSystemPromptLength} символов)" });

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var settings = await _db.UserSettings.FirstOrDefaultAsync(s => s.UserId == userId);

        if (settings == null)
        {
            settings = new UserSettings { UserId = userId };
            _db.UserSettings.Add(settings);
        }

        if (req.DisplayName is not null)
            settings.DisplayName = req.DisplayName == "" ? null : req.DisplayName;
        if (req.AssistantName is not null)
            settings.AssistantName = req.AssistantName == "" ? null : req.AssistantName;
        if (req.AvatarId is not null)
            settings.AvatarId = req.AvatarId;
        if (req.InterfaceLanguage is not null)
            settings.InterfaceLanguage = req.InterfaceLanguage;

        // Only update keys if a new value is provided and it isn't just the
        // masked placeholder being echoed back unchanged -- an EXACT match
        // against what MaskKey would currently produce, not the previous
        // Contains("...") substring check, which could misfire on a real key
        // that happens to contain the literal characters "..."
        // (PROJECT-AUDIT-2026-07-10 API-01).
        if (req.OpenAiApiKey is not null && req.OpenAiApiKey != MaskKey(settings.OpenAiApiKey))
            settings.OpenAiApiKey = req.OpenAiApiKey == "" ? null : req.OpenAiApiKey;

        if (req.AnthropicApiKey is not null && req.AnthropicApiKey != MaskKey(settings.AnthropicApiKey))
            settings.AnthropicApiKey = req.AnthropicApiKey == "" ? null : req.AnthropicApiKey;

        if (req.GeminiApiKey is not null && req.GeminiApiKey != MaskKey(settings.GeminiApiKey))
            settings.GeminiApiKey = req.GeminiApiKey == "" ? null : req.GeminiApiKey;

        if (req.CustomSystemPrompt is not null)
            settings.CustomSystemPrompt = req.CustomSystemPrompt == "" ? null : req.CustomSystemPrompt;
        if (req.FullTranslation is not null)
            settings.FullTranslation = req.FullTranslation.Value;
        settings.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        return Ok(new SettingsResponse(
            settings.DisplayName,
            settings.AssistantName,
            settings.AvatarId,
            settings.InterfaceLanguage,
            MaskKey(settings.OpenAiApiKey),
            MaskKey(settings.AnthropicApiKey),
            MaskKey(settings.GeminiApiKey),
            settings.CustomSystemPrompt,
            settings.FullTranslation
        ));
    }

    [HttpGet("default-prompt")]
    public IActionResult GetDefaultPrompt()
    {
        return Ok(new { prompt = _tutor.GetDefaultSystemPromptText() });
    }

    private static string? MaskKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length < 8) return null;
        return key[..3] + "..." + key[^4..];
    }
}
