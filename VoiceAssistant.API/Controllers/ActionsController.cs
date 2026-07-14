using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VoiceAssistant.API.Services;

namespace VoiceAssistant.API.Controllers;

[ApiController]
[Route("api/v1/actions")]
[Authorize]
public sealed class ActionsController : ControllerBase
{
    private readonly ActionReceiptService _receipts;

    public ActionsController(ActionReceiptService receipts)
    {
        _receipts = receipts;
    }

    public record RecordReceiptRequest(Guid ActionId, string Status, string? ResultCode = null);

    [HttpPost("receipts")]
    public async Task<ActionResult<ActionReceiptResponse>> RecordReceipt(
        [FromBody] RecordReceiptRequest request,
        CancellationToken cancellationToken)
    {
        var receipt = await _receipts.RecordAsync(
            User.FindFirstValue(ClaimTypes.NameIdentifier)!,
            request.ActionId,
            request.Status,
            request.ResultCode,
            cancellationToken);
        return receipt is null ? NotFound() : Ok(receipt);
    }
}
