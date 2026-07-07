using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using VoiceAssistant.API.Data;
using VoiceAssistant.API.Data.Entities;

namespace VoiceAssistant.API.Controllers;

[ApiController]
[Route("api/v1/auth")]
public class AuthController : ControllerBase
{
    private readonly UserManager<User> _userManager;
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    private static readonly TimeSpan DeviceLinkCodeLifetime = TimeSpan.FromMinutes(10);

    public AuthController(UserManager<User> userManager, AppDbContext db, IConfiguration config)
    {
        _userManager = userManager;
        _db = db;
        _config = config;
    }

    public record RegisterRequest(string Email, string Password, string? NativeLang);
    public record LoginRequest(string Email, string Password);

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest req)
    {
        var user = new User
        {
            UserName = req.Email,
            Email = req.Email,
            NativeLang = req.NativeLang ?? "uk"
        };

        var result = await _userManager.CreateAsync(user, req.Password);
        if (!result.Succeeded)
            return BadRequest(new { errors = result.Errors.Select(e => e.Description) });

        return Ok(new { token = GenerateToken(user) });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        var user = await _userManager.FindByEmailAsync(req.Email);
        if (user == null || !await _userManager.CheckPasswordAsync(user, req.Password))
            return Unauthorized(new { error = "Invalid credentials" });

        user.LastActiveAt = DateTime.UtcNow;
        await _userManager.UpdateAsync(user);

        return Ok(new { token = GenerateToken(user) });
    }

    // Elderly-friendly login: an already-logged-in device (e.g. a family
    // member's phone) generates a short-lived code here, which a brand-new
    // device can redeem for a real token via device-link/redeem below —
    // without anyone typing an email/password on the new device.
    [Authorize]
    [HttpPost("device-link")]
    public async Task<IActionResult> CreateDeviceLink()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

        string code;
        do
        {
            code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        } while (await _db.DeviceLinkCodes.AnyAsync(d => d.Code == code && !d.Used && d.ExpiresAt > DateTime.UtcNow));

        var expiresAt = DateTime.UtcNow.Add(DeviceLinkCodeLifetime);
        _db.DeviceLinkCodes.Add(new DeviceLinkCode { UserId = userId, Code = code, ExpiresAt = expiresAt });
        await _db.SaveChangesAsync();

        return Ok(new { code, expiresAt });
    }

    public record RedeemDeviceLinkRequest(string Code);

    [HttpPost("device-link/redeem")]
    public async Task<IActionResult> RedeemDeviceLink([FromBody] RedeemDeviceLinkRequest req)
    {
        var link = await _db.DeviceLinkCodes.FirstOrDefaultAsync(d => d.Code == req.Code);
        if (link == null || link.Used || link.ExpiresAt <= DateTime.UtcNow)
            return BadRequest(new { error = "Код недействителен или истёк" });

        link.Used = true;
        await _db.SaveChangesAsync();

        var user = await _userManager.FindByIdAsync(link.UserId);
        if (user == null) return BadRequest(new { error = "Код недействителен или истёк" });

        user.LastActiveAt = DateTime.UtcNow;
        await _userManager.UpdateAsync(user);

        return Ok(new { token = GenerateToken(user) });
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var user = await _userManager.FindByIdAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (user == null) return Unauthorized();

        return Ok(new
        {
            user.Id,
            user.Email,
            user.NativeLang,
            user.Level,
            user.DisplayName,
            user.CreatedAt,
            user.LastActiveAt
        });
    }

    public record UpdateProfileRequest(string DisplayName);

    // Sets the name the assistant calls the user by — collected during the
    // mobile onboarding screen (or later from settings), fed into
    // CompanionPromptService.GetSystemPrompt via ChatController.Send.
    [Authorize]
    [HttpPut("me")]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.DisplayName))
            return BadRequest(new { error = "Имя не может быть пустым" });

        var user = await _userManager.FindByIdAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (user == null) return Unauthorized();

        user.DisplayName = req.DisplayName.Trim();
        await _userManager.UpdateAsync(user);

        return NoContent();
    }

    private string GenerateToken(User user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_config["Jwt:Secret"]!));
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Email, user.Email!)
        };

        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"],
            audience: _config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddDays(30),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
