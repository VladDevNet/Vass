using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VoiceAssistant.API.Data;
using VoiceAssistant.API.Data.Entities;
using VoiceAssistant.API.Services;

namespace VoiceAssistant.API.Controllers;

[ApiController]
[Route("api/settings")]
[Authorize]
public class SettingsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly CompanionPromptService _tutor;

    public SettingsController(AppDbContext db, CompanionPromptService tutor)
    {
        _db = db;
        _tutor = tutor;
    }

    public record SettingsResponse(
        string? DisplayName,
        string InterfaceLanguage,
        string? OpenAiApiKey,
        string? AnthropicApiKey,
        string? GeminiApiKey,
        string? CustomSystemPrompt,
        bool FullTranslation);

    public record SettingsUpdateRequest(
        string? DisplayName,
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
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var settings = await _db.UserSettings.FirstOrDefaultAsync(s => s.UserId == userId);

        if (settings == null)
        {
            settings = new UserSettings { UserId = userId };
            _db.UserSettings.Add(settings);
        }

        settings.DisplayName = req.DisplayName;
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
