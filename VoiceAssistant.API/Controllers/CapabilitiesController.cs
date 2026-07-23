using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VoiceAssistant.API.Services;

namespace VoiceAssistant.API.Controllers;

[ApiController]
[Route("api/v1/capabilities")]
[Authorize]
public class CapabilitiesController : ControllerBase
{
    private readonly AssistantCapabilityRegistry _capabilities;
    private readonly CapabilityDiscoveryService _discovery;

    public CapabilitiesController(AssistantCapabilityRegistry capabilities, CapabilityDiscoveryService discovery)
    {
        _capabilities = capabilities;
        _discovery = discovery;
    }

    // This is presentation-only. The model receives the authoritative
    // per-turn manifest in ChatController; the mobile app uses this endpoint
    // to render the same help catalog in its settings surface.
    [HttpGet("help")]
    public ActionResult<IReadOnlyList<AssistantCapabilityHelpItem>> GetHelp(
        [FromQuery] bool supportsReminders,
        [FromQuery] bool supportsPeriodicReminders,
        [FromQuery] bool supportsExternalActions,
        [FromQuery] bool supportsScreenAnalysis,
        [FromQuery] bool supportsLibrary,
        [FromQuery] string? topic = null)
    {
        var context = new AssistantRuntimeContext(
            HasVisualAttachment: false,
            SupportsScreenAnalysis: supportsScreenAnalysis,
            SupportsExternalActions: supportsExternalActions,
            SupportsReminders: supportsReminders,
            SupportsPeriodicReminders: supportsPeriodicReminders,
            SupportsLibrary: supportsLibrary);
        return Ok(_capabilities.GetHelp(context, topic));
    }

    public record RecordCapabilityUsageRequest(string CapabilityId);

    [HttpGet("discovery")]
    public async Task<ActionResult<CapabilityDiscoverySnapshot>> GetDiscovery(
        [FromQuery] bool supportsReminders,
        [FromQuery] bool supportsPeriodicReminders,
        [FromQuery] bool supportsExternalActions,
        [FromQuery] bool supportsScreenAnalysis,
        [FromQuery] bool supportsLibrary,
        CancellationToken cancellationToken)
    {
        var context = new AssistantRuntimeContext(
            HasVisualAttachment: false,
            SupportsScreenAnalysis: supportsScreenAnalysis,
            SupportsExternalActions: supportsExternalActions,
            SupportsReminders: supportsReminders,
            SupportsPeriodicReminders: supportsPeriodicReminders,
            SupportsLibrary: supportsLibrary);
        return Ok(await _discovery.GetSnapshotAsync(
            User.FindFirstValue(ClaimTypes.NameIdentifier)!,
            context,
            cancellationToken));
    }

    // Only user-operated client surfaces report here. Server-side tool and
    // receipt outcomes are recorded internally, so a client cannot invent a
    // memory/reminder/YouTube success by posting an arbitrary feature ID.
    [HttpPost("usage")]
    public async Task<IActionResult> RecordUsage(
        [FromBody] RecordCapabilityUsageRequest request,
        CancellationToken cancellationToken)
    {
        var recorded = await _discovery.MarkClientReportedUseAsync(
            User.FindFirstValue(ClaimTypes.NameIdentifier)!,
            request.CapabilityId,
            cancellationToken);
        return recorded ? NoContent() : BadRequest(new { error = "Неизвестная пользовательская возможность." });
    }

    // The client calls this only on the first authenticated launch of a new
    // build. The service atomically selects and records features that have
    // never been used or introduced to this person before.
    [HttpPost("discovery/release-introduction")]
    public async Task<ActionResult<IReadOnlyList<CapabilityDiscoveryItem>>> PresentReleaseIntroduction(
        [FromQuery] bool supportsReminders,
        [FromQuery] bool supportsPeriodicReminders,
        [FromQuery] bool supportsExternalActions,
        [FromQuery] bool supportsScreenAnalysis,
        [FromQuery] bool supportsLibrary,
        CancellationToken cancellationToken)
    {
        var context = new AssistantRuntimeContext(
            HasVisualAttachment: false,
            SupportsScreenAnalysis: supportsScreenAnalysis,
            SupportsExternalActions: supportsExternalActions,
            SupportsReminders: supportsReminders,
            SupportsPeriodicReminders: supportsPeriodicReminders,
            SupportsLibrary: supportsLibrary);
        return Ok(await _discovery.PresentReleaseIntroductionAsync(
            User.FindFirstValue(ClaimTypes.NameIdentifier)!,
            context,
            cancellationToken));
    }
}
