using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VoiceAssistant.API.Data;
using VoiceAssistant.API.Data.Entities;

namespace VoiceAssistant.API.Controllers;

[ApiController]
[Route("api/onboarding")]
[Authorize]
public class OnboardingController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly UserManager<User> _userManager;

    public OnboardingController(AppDbContext db, UserManager<User> userManager)
    {
        _db = db;
        _userManager = userManager;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var hasTestResult = await _db.TestResults.AnyAsync(t => t.UserId == userId);
        var user = await _userManager.FindByIdAsync(userId);

        return Ok(new
        {
            needsOnboarding = !hasTestResult,
            level = user?.Level ?? "A1"
        });
    }

    public record SetLevelRequest(string Level);

    [HttpPost("set-level")]
    public async Task<IActionResult> SetLevel([FromBody] SetLevelRequest req)
    {
        var validLevels = new[] { "A1", "A2", "B1", "B2" };
        if (!validLevels.Contains(req.Level))
            return BadRequest(new { error = "Invalid level. Use A1, A2, B1, or B2." });

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null) return Unauthorized();

        user.Level = req.Level;
        await _userManager.UpdateAsync(user);

        // Create a TestResult so onboarding won't show again
        _db.TestResults.Add(new TestResult
        {
            UserId = userId,
            TestType = "manual",
            Level = req.Level,
            Score = 0,
            MaxScore = 0,
            AnswersJson = "{}"
        });
        await _db.SaveChangesAsync();

        return Ok(new { level = req.Level });
    }
}
