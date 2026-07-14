using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VoiceAssistant.API.Data;
using VoiceAssistant.API.Data.Entities;

namespace VoiceAssistant.API.Services;

public static class AssistantActionTaxonomies
{
    public const string ServerLocal = "server_local";
    public const string Navigation = "navigation";
    public const string External = "external";
    public const string ProviderHosted = "provider_hosted";
    public const string UserControl = "user_control";
}

public static class ActionReceiptStatuses
{
    public const string Proposed = "proposed";
    public const string HandlerDispatched = "handler_dispatched";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public sealed record ActionProposal(Guid ActionId, string Type, string Taxonomy, string? Query, string? VideoId);
public sealed record ActionReceiptResponse(Guid ActionId, string Type, string Taxonomy, string Status, string? ResultCode);

public sealed class ActionReceiptService
{
    private static readonly Regex ResultCodePattern = new(@"\A[a-z0-9_]{1,64}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public ActionReceiptService(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public static string? GetTaxonomy(string actionType) => actionType switch
    {
        ExternalActionTypes.OpenVass => AssistantActionTaxonomies.Navigation,
        ExternalActionTypes.YouTubeSearch or ExternalActionTypes.YouTubeWatch => AssistantActionTaxonomies.External,
        _ => null
    };

    public async Task<ActionProposal?> ProposeAsync(
        string userId, int sourceMessageId, ExternalActionCommand command, CancellationToken cancellationToken)
    {
        var taxonomy = GetTaxonomy(command.Type);
        if (taxonomy is null) return null;

        var receipt = new ActionReceipt
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            SourceMessageId = sourceMessageId,
            ActionType = command.Type,
            Taxonomy = taxonomy,
        };
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        db.ActionReceipts.Add(receipt);
        await db.SaveChangesAsync(cancellationToken);
        return new(receipt.Id, command.Type, taxonomy, command.Query, command.VideoId);
    }

    public async Task<ActionReceiptResponse?> RecordAsync(
        string userId, Guid actionId, string status, string? resultCode, CancellationToken cancellationToken)
    {
        if (status is not (ActionReceiptStatuses.HandlerDispatched or ActionReceiptStatuses.Failed or ActionReceiptStatuses.Cancelled))
            return null;
        if (resultCode is not null && !ResultCodePattern.IsMatch(resultCode))
            return null;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var receipt = await db.ActionReceipts.SingleOrDefaultAsync(item => item.Id == actionId && item.UserId == userId, cancellationToken);
        if (receipt is null) return null;
        if (receipt.Status != ActionReceiptStatuses.Proposed && receipt.Status != status)
            return null;

        receipt.Status = status;
        receipt.ResultCode = resultCode;
        receipt.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return new(receipt.Id, receipt.ActionType, receipt.Taxonomy, receipt.Status, receipt.ResultCode);
    }
}
