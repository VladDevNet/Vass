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

    public CapabilitiesController(AssistantCapabilityRegistry capabilities)
    {
        _capabilities = capabilities;
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
        [FromQuery] string? topic = null)
    {
        var context = new AssistantRuntimeContext(
            HasVisualAttachment: false,
            SupportsScreenAnalysis: supportsScreenAnalysis,
            SupportsExternalActions: supportsExternalActions,
            SupportsReminders: supportsReminders,
            SupportsPeriodicReminders: supportsPeriodicReminders);
        return Ok(_capabilities.GetHelp(context, topic));
    }
}
