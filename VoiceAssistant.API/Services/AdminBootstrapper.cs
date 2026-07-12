using Microsoft.AspNetCore.Identity;
using VoiceAssistant.API.Data.Entities;

namespace VoiceAssistant.API.Services;

public static class AdminBootstrapper
{
    public const string RoleName = "Admin";

    public static async Task EnsureAdminAsync(IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var email = configuration["Admin:Email"]?.Trim();
        if (string.IsNullOrWhiteSpace(email)) return;

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("AdminBootstrap");
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = services.GetRequiredService<UserManager<User>>();

        if (!await roleManager.RoleExistsAsync(RoleName))
        {
            var roleResult = await roleManager.CreateAsync(new IdentityRole(RoleName));
            if (!roleResult.Succeeded)
                throw new InvalidOperationException($"Failed to create Admin role: {string.Join(", ", roleResult.Errors.Select(e => e.Description))}");
        }

        var user = await userManager.FindByEmailAsync(email);
        if (user is null)
        {
            logger.LogWarning("Admin bootstrap account {Email} does not exist. Register it first, then restart the API.", email);
            return;
        }

        if (await userManager.IsInRoleAsync(user, RoleName)) return;

        var addResult = await userManager.AddToRoleAsync(user, RoleName);
        if (!addResult.Succeeded)
            throw new InvalidOperationException($"Failed to assign Admin role: {string.Join(", ", addResult.Errors.Select(e => e.Description))}");

        var stampResult = await userManager.UpdateSecurityStampAsync(user);
        if (!stampResult.Succeeded)
            throw new InvalidOperationException($"Failed to revoke existing admin tokens: {string.Join(", ", stampResult.Errors.Select(e => e.Description))}");

        logger.LogInformation("Assigned Admin role to {Email}; existing tokens were revoked", email);
    }
}
