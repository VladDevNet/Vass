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

    [HttpPut]
    public async Task<IActionResult> Update([FromBody] SettingsUpdateRequest req)
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

        settings.DisplayName = req.DisplayName;
        settings.AssistantName = req.AssistantName;
        settings.AvatarId = req.AvatarId;
        if (req.InterfaceLanguage is not null)
            settings.InterfaceLanguage = req.InterfaceLanguage;

        // Only update keys if a new value is provided (not masked)
        if (req.OpenAiApiKey is not null && !req.OpenAiApiKey.Contains("..."))
            settings.OpenAiApiKey = req.OpenAiApiKey == "" ? null : req.OpenAiApiKey;

        if (req.AnthropicApiKey is not null && !req.AnthropicApiKey.Contains("..."))
            settings.AnthropicApiKey = req.AnthropicApiKey == "" ? null : req.AnthropicApiKey;

        if (req.GeminiApiKey is not null && !req.GeminiApiKey.Contains("..."))
            settings.GeminiApiKey = req.GeminiApiKey == "" ? null : req.GeminiApiKey;

        settings.CustomSystemPrompt = req.CustomSystemPrompt;
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
