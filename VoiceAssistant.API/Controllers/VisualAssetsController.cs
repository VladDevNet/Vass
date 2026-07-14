using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VoiceAssistant.API.Data;
using VoiceAssistant.API.Data.Entities;
using VoiceAssistant.API.Services;

namespace VoiceAssistant.API.Controllers;

[ApiController]
[Route("api/v1/chat/visual-assets")]
[Authorize]
public class VisualAssetsController : ControllerBase
{
    public const long MaxAttachmentSize = ImageContentInspector.MaxAttachmentSize;
    public const long MaxAttachmentRequestBodySize = MaxAttachmentSize + 64 * 1024;

    private readonly AppDbContext _db;
    private readonly VisualAssetService _assets;
    private readonly ILogger<VisualAssetsController> _logger;

    public VisualAssetsController(AppDbContext db, VisualAssetService assets, ILogger<VisualAssetsController> logger)
    {
        _db = db;
        _assets = assets;
        _logger = logger;
    }

    [HttpPost]
    [RequestSizeLimit(MaxAttachmentRequestBodySize)]
    public async Task<IActionResult> Upload(IFormFile file)
    {
        if (file.Length == 0 || file.Length > MaxAttachmentSize)
            return BadRequest(new { error = "Вложение должно быть размером от 1 байта до 50 МБ." });

        if (!ImageContentInspector.TryNormalizeAttachmentMimeType(file.ContentType, out var mimeType))
            return BadRequest(new { error = "Не удалось определить тип вложения." });

        await using var input = file.OpenReadStream();
        using var buffer = new MemoryStream((int)file.Length);
        await input.CopyToAsync(buffer, HttpContext.RequestAborted);
        var content = buffer.ToArray();

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var id = Guid.NewGuid();
        var storageFileName = VisualAssetService.CreateStorageFileName(id, mimeType);
        var originalFileName = Path.GetFileName(file.FileName);
        if (originalFileName.Length > 255) originalFileName = originalFileName[..255];
        var asset = new VisualAsset
        {
            Id = id,
            UserId = userId,
            StorageFileName = storageFileName,
            MimeType = mimeType,
            SizeBytes = content.LongLength,
            OriginalFileName = string.IsNullOrWhiteSpace(originalFileName) ? null : originalFileName,
        };

        try
        {
            await _assets.WriteAsync(storageFileName, content, HttpContext.RequestAborted);
            _db.VisualAssets.Add(asset);
            await _db.SaveChangesAsync(HttpContext.RequestAborted);
        }
        catch
        {
            try { await _assets.DeleteIfExistsAsync(storageFileName); } catch { }
            throw;
        }

        _logger.LogInformation("Attachment uploaded: AssetId={AssetId}, MimeType={MimeType}, SizeBytes={SizeBytes}",
            asset.Id, asset.MimeType, asset.SizeBytes);
        return Ok(new { asset.Id, asset.MimeType, asset.SizeBytes, asset.OriginalFileName });
    }

    [HttpGet("{id:guid}/content")]
    public async Task<IActionResult> GetContent(Guid id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var asset = await _db.VisualAssets
            .Where(item => item.Id == id && item.UserId == userId)
            .Select(item => new { item.StorageFileName, item.MimeType, item.OriginalFileName })
            .FirstOrDefaultAsync(HttpContext.RequestAborted);
        if (asset is null || !_assets.TryOpenRead(asset.StorageFileName, out var stream)) return NotFound();

        Response.Headers.Append("X-Content-Type-Options", "nosniff");
        return File(stream!, asset.MimeType, asset.OriginalFileName ?? "attachment", enableRangeProcessing: true);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var asset = await _db.VisualAssets
            .FirstOrDefaultAsync(item => item.Id == id && item.UserId == userId, HttpContext.RequestAborted);
        if (asset is null) return NotFound();

        var isAttached = await _db.MessageAttachments
            .AnyAsync(item => item.VisualAssetId == id, HttpContext.RequestAborted);
        if (isAttached) return Conflict(new { error = "Вложение уже прикреплено к сообщению." });

        _db.VisualAssets.Remove(asset);
        await _db.SaveChangesAsync(HttpContext.RequestAborted);
        try
        {
            await _assets.DeleteIfExistsAsync(asset.StorageFileName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete visual asset file after database cleanup: AssetId={AssetId}", id);
        }
        return NoContent();
    }
}
