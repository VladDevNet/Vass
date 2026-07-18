using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace VoiceAssistant.API.Controllers;

public sealed record AndroidUpdateResponse(
    bool UpdateAvailable,
    bool Mandatory,
    string? LatestVersion,
    int LatestVersionCode,
    int MinimumSupportedVersionCode,
    string? DownloadUrl,
    string? Sha256,
    string? ReleaseNotes);

public sealed record AndroidUpdateRelease(
    string? LatestVersion,
    int LatestVersionCode,
    int MinimumSupportedVersionCode,
    string? DownloadUrl,
    string? Sha256,
    string? ReleaseNotes);

[ApiController]
[Route("api/v1/app-updates")]
[AllowAnonymous]
public sealed class AppUpdatesController : ControllerBase
{
    private readonly IConfiguration _config;

    public AppUpdatesController(IConfiguration config)
    {
        _config = config;
    }

    // Anonymous on purpose: a device must still be able to learn about a
    // mandatory update when its saved token has expired or it has not logged
    // in yet. The endpoint contains only public release metadata.
    [HttpGet("android")]
    public ActionResult<AndroidUpdateResponse> GetAndroid([FromQuery] int currentVersionCode)
    {
        if (currentVersionCode < 0)
        {
            return BadRequest(new { error = "currentVersionCode must not be negative." });
        }

        var section = _config.GetSection("MobileUpdates:Android");
        var release = new AndroidUpdateRelease(
            section["LatestVersion"],
            section.GetValue("LatestVersionCode", 0),
            section.GetValue("MinimumSupportedVersionCode", 0),
            section["DownloadUrl"],
            section["Sha256"],
            section["ReleaseNotes"]);

        return Ok(BuildAndroidResponse(currentVersionCode, release));
    }

    public static AndroidUpdateResponse BuildAndroidResponse(int currentVersionCode, AndroidUpdateRelease release)
    {
        var latestVersionCode = Math.Max(0, release.LatestVersionCode);
        var minimumSupportedVersionCode = Math.Clamp(release.MinimumSupportedVersionCode, 0, latestVersionCode);
        var latestVersion = release.LatestVersion?.Trim();
        var downloadUrl = NormalizeHttpsUrl(release.DownloadUrl);
        var sha256 = NormalizeSha256(release.Sha256);

        // A malformed deployment setting must fail closed. In particular, do
        // not lock a user behind a "mandatory" modal that has nowhere safe to
        // send them for an APK.
        var hasPublishedRelease = latestVersionCode > 0 &&
                                  !string.IsNullOrWhiteSpace(latestVersion) &&
                                  downloadUrl is not null;
        var updateAvailable = hasPublishedRelease && currentVersionCode < latestVersionCode;

        return new AndroidUpdateResponse(
            updateAvailable,
            updateAvailable && currentVersionCode < minimumSupportedVersionCode,
            hasPublishedRelease ? latestVersion : null,
            hasPublishedRelease ? latestVersionCode : 0,
            hasPublishedRelease ? minimumSupportedVersionCode : 0,
            hasPublishedRelease ? downloadUrl : null,
            hasPublishedRelease ? sha256 : null,
            hasPublishedRelease ? NormalizeText(release.ReleaseNotes) : null);
    }

    private static string? NormalizeHttpsUrl(string? value)
    {
        var candidate = value?.Trim();
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
               uri.Scheme == Uri.UriSchemeHttps
            ? uri.AbsoluteUri
            : null;
    }

    private static string? NormalizeSha256(string? value)
    {
        var candidate = value?.Trim();
        return candidate is { Length: 64 } && candidate.All(Uri.IsHexDigit)
            ? candidate.ToLowerInvariant()
            : null;
    }

    private static string? NormalizeText(string? value)
    {
        var candidate = value?.Trim();
        return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
    }
}
