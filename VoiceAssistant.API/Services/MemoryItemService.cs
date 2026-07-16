using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using VoiceAssistant.API.Data;
using VoiceAssistant.API.Data.Entities;

namespace VoiceAssistant.API.Services;

public sealed record MemoryStatusResponse(string Availability, int ActiveCount, bool SemanticSearchAvailable);
public sealed record MemoryAttachmentResponse(Guid Id, string MimeType, long SizeBytes, string? OriginalFileName);
public sealed record MemoryItemResponse(
    Guid Id, string Text, string Kind, string Category, int Revision, string Status,
    DateTime CreatedAt, DateTime UpdatedAt, DateTime? LastRecalledAt, string EmbeddingState,
    MemoryAttachmentResponse? Attachment);
public sealed record MemorySearchResponse(string Status, string Retrieval, IReadOnlyList<MemoryItemResponse> Items);
public sealed record MemoryOperationResult(
    Guid OperationId, string Status, string Code, Guid? MemoryItemId = null,
    string? ConfirmationToken = null, DateTime? ConfirmationExpiresAt = null);

// Server-side adapter for the transitional canonical memory store. Each
// mutating operation gets a durable receipt before optional embedding work.
public sealed class MemoryItemService
{
    private const int MaxTextLength = 1000;
    private static readonly TimeSpan ClearConfirmationLifetime = TimeSpan.FromMinutes(10);
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly GeminiService _gemini;
    private readonly ILogger<MemoryItemService> _logger;
    private readonly bool _enabled;

    public MemoryItemService(
        IDbContextFactory<AppDbContext> dbFactory,
        GeminiService gemini,
        IConfiguration configuration,
        ILogger<MemoryItemService> logger)
    {
        _dbFactory = dbFactory;
        _gemini = gemini;
        _logger = logger;
        _enabled = configuration.GetValue("Features:LongTermMemoryEnabled", true);
    }

    public async Task<MemoryStatusResponse> GetStatusAsync(string userId, CancellationToken cancellationToken)
    {
        if (!_enabled) return new("disabled", 0, false);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var count = await db.MemoryItems.CountAsync(item => item.UserId == userId && item.Status == "active", cancellationToken);
        return new("available", count, db.Database.IsNpgsql());
    }

    public async Task<IReadOnlyList<MemoryItemResponse>> ListAsync(string userId, int limit, CancellationToken cancellationToken)
    {
        if (!_enabled) return [];
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.MemoryItems.AsNoTracking()
            .Include(item => item.VisualAsset)
            .Where(item => item.UserId == userId && item.Status == "active")
            .OrderByDescending(item => item.UpdatedAt)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(item => ToResponse(item))
            .ToListAsync(cancellationToken);
    }

    public async Task<MemorySearchResponse> SearchAsync(string userId, string query, CancellationToken cancellationToken)
    {
        var normalized = NormalizeText(query);
        if (!_enabled) return new("disabled", "none", []);
        if (normalized.Length == 0) return new("invalid", "none", []);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var needle = normalized.ToLowerInvariant();
        var lexical = await db.MemoryItems.AsNoTracking()
            .Include(item => item.VisualAsset)
            .Where(item => item.UserId == userId && item.Status == "active" && item.Text.ToLower().Contains(needle))
            .OrderByDescending(item => item.UpdatedAt)
            .Take(20)
            .ToListAsync(cancellationToken);

        // Hybrid when pgvector and an embedding are available. Lexical search
        // remains a useful, explicit fallback if the provider is unavailable.
        var candidates = lexical.ToDictionary(item => item.Id, item => (Item: item, Score: 0.7));
        var usedSemantic = false;
        if (db.Database.IsNpgsql())
        {
            try
            {
                var apiKey = await db.UserSettings.Where(s => s.UserId == userId)
                    .Select(s => s.GeminiApiKey).FirstOrDefaultAsync(cancellationToken);
                var vector = new Vector(await _gemini.GenerateEmbeddingAsync(
                    $"task: search result | query: {normalized}", apiKey, cancellationToken));
                var semantic = await db.MemoryItems
                    .Include(item => item.VisualAsset)
                    .Where(item => item.UserId == userId && item.Status == "active" &&
                                   item.EmbeddingState == "ready" && item.EmbeddingModel == GeminiService.EmbeddingModel &&
                                   item.Embedding != null)
                    .Select(item => new { Item = item, Distance = item.Embedding!.CosineDistance(vector) })
                    .Where(hit => hit.Distance <= LongTermMemoryService.MaxCosineDistance)
                    .OrderBy(hit => hit.Distance)
                    .Take(20)
                    .ToListAsync(cancellationToken);
                foreach (var hit in semantic)
                {
                    var score = Math.Max(0.01, 1 - hit.Distance);
                    if (!candidates.TryGetValue(hit.Item.Id, out var current) || score > current.Score)
                        candidates[hit.Item.Id] = (hit.Item, score);
                }
                usedSemantic = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Semantic memory search unavailable for user {UserId}; using lexical results", userId);
            }
        }

        var results = candidates.Values
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Item.UpdatedAt)
            .Take(20)
            .Select(candidate => ToResponse(candidate.Item))
            .ToList();
        return new(results.Count == 0 ? "not_found" : "ok", usedSemantic ? "hybrid" : "lexical", results);
    }

    public async Task<MemoryOperationResult> RememberAsync(
        string userId,
        string text,
        int? sourceMessageId,
        Guid? operationId,
        CancellationToken cancellationToken,
        string? category = null,
        Guid? visualAssetId = null)
    {
        var normalized = NormalizeText(text);
        var normalizedCategory = MemoryCategories.NormalizeOrDefault(
            category,
            visualAssetId is null ? MemoryCategories.Other : "documents");
        var arguments = $"{normalized}\n{normalizedCategory}\n{visualAssetId?.ToString("N")}";
        return await MutateAsync(userId, "remember", operationId, arguments, async (db, operation) =>
        {
            if (!_enabled) return Complete(operation, "rejected", "memory_disabled");
            if (normalized.Length == 0) return Complete(operation, "rejected", "invalid_text");
            if (LooksSensitive(normalized)) return Complete(operation, "rejected", "sensitive_content_not_allowed");
            if (!string.IsNullOrWhiteSpace(category) && !MemoryCategories.IsValid(category))
                return Complete(operation, "rejected", "invalid_category");
            if (visualAssetId is not null && !await db.VisualAssets.AnyAsync(asset =>
                    asset.Id == visualAssetId && asset.UserId == userId, cancellationToken))
                return Complete(operation, "rejected", "visual_asset_not_found");

            var hash = LongTermMemoryService.ComputeContentHash(
                visualAssetId is null ? normalized : $"{normalized}\n{visualAssetId:N}");
            var existing = await db.MemoryItems.SingleOrDefaultAsync(item => item.UserId == userId && item.ContentHash == hash, cancellationToken);
            if (existing?.Status == "active") return Complete(operation, "completed", "already_known", existing.Id);
            if (existing is not null)
            {
                existing.Status = "active";
                existing.TombstonedAt = null;
                existing.UpdatedAt = DateTime.UtcNow;
                existing.Revision++;
                existing.SourceMessageId = sourceMessageId ?? existing.SourceMessageId;
                existing.Category = normalizedCategory;
                existing.VisualAssetId = visualAssetId ?? existing.VisualAssetId;
                existing.Kind = existing.VisualAssetId is null ? "semantic_fact" : "attachment";
                operation.MemoryItemId = existing.Id;
                await db.SaveChangesAsync(cancellationToken);
                await PopulateEmbeddingAsync(existing.Id, userId, cancellationToken);
                return Complete(operation, "completed", "remembered", existing.Id);
            }

            var item = new MemoryItem
            {
                UserId = userId,
                Text = normalized,
                ContentHash = hash,
                SourceMessageId = sourceMessageId,
                Category = normalizedCategory,
                VisualAssetId = visualAssetId,
                Kind = visualAssetId is null ? "semantic_fact" : "attachment",
                EmbeddingState = "pending"
            };
            db.MemoryItems.Add(item);
            operation.MemoryItemId = item.Id;
            await db.SaveChangesAsync(cancellationToken); // durable receipt precedes optional embedding
            await PopulateEmbeddingAsync(item.Id, userId, cancellationToken);
            return Complete(operation, "completed", "remembered", item.Id);
        }, cancellationToken);
    }

    public async Task<MemoryOperationResult> CorrectAsync(
        string userId,
        Guid id,
        string text,
        Guid? operationId,
        CancellationToken cancellationToken,
        string? category = null)
    {
        var normalized = NormalizeText(text);
        var normalizedCategory = MemoryCategories.NormalizeOrDefault(category);
        return await MutateAsync(userId, "correct", operationId, $"{id:N}:{normalized}:{category}", async (db, operation) =>
        {
            if (!_enabled) return Complete(operation, "rejected", "memory_disabled");
            if (normalized.Length == 0 || LooksSensitive(normalized)) return Complete(operation, "rejected", "invalid_text");
            if (!string.IsNullOrWhiteSpace(category) && !MemoryCategories.IsValid(category))
                return Complete(operation, "rejected", "invalid_category");
            var oldItem = await db.MemoryItems.SingleOrDefaultAsync(item => item.Id == id && item.UserId == userId && item.Status == "active", cancellationToken);
            if (oldItem is null) return Complete(operation, "completed", "not_found");

            var replacementHash = LongTermMemoryService.ComputeContentHash(normalized);
            var duplicate = await db.MemoryItems.AnyAsync(item => item.UserId == userId && item.Id != id &&
                item.ContentHash == replacementHash && item.Status == "active", cancellationToken);
            if (duplicate) return Complete(operation, "completed", "already_known");

            oldItem.Status = "superseded";
            oldItem.TombstonedAt = DateTime.UtcNow;
            oldItem.UpdatedAt = DateTime.UtcNow;
            oldItem.Revision++;
            var inheritedVisualAssetId = oldItem.VisualAssetId;
            oldItem.VisualAssetId = null;
            var replacement = new MemoryItem
            {
                UserId = userId,
                Text = normalized,
                ContentHash = replacementHash,
                SupersedesMemoryItemId = oldItem.Id,
                Revision = oldItem.Revision,
                SourceMessageId = oldItem.SourceMessageId,
                Category = string.IsNullOrWhiteSpace(category) ? oldItem.Category : normalizedCategory,
                VisualAssetId = inheritedVisualAssetId,
                Kind = oldItem.Kind,
                EmbeddingState = "pending"
            };
            db.MemoryItems.Add(replacement);
            operation.MemoryItemId = replacement.Id;
            await db.SaveChangesAsync(cancellationToken);
            await PopulateEmbeddingAsync(replacement.Id, userId, cancellationToken);
            return Complete(operation, "completed", "corrected", replacement.Id);
        }, cancellationToken);
    }

    public async Task<MemoryOperationResult> ForgetAsync(string userId, Guid id, Guid? operationId, CancellationToken cancellationToken) =>
        await MutateAsync(userId, "forget", operationId, id.ToString("N"), async (db, operation) =>
        {
            if (!_enabled) return Complete(operation, "rejected", "memory_disabled");
            var item = await db.MemoryItems.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.UserId == userId && candidate.Status == "active", cancellationToken);
            if (item is null) return Complete(operation, "completed", "not_found");
            item.Status = "tombstoned";
            item.TombstonedAt = DateTime.UtcNow;
            item.UpdatedAt = DateTime.UtcNow;
            item.Revision++;
            item.VisualAssetId = null;
            operation.MemoryItemId = item.Id;
            await db.SaveChangesAsync(cancellationToken);
            return Complete(operation, "completed", "forgotten", item.Id);
        }, cancellationToken);

    public async Task<MemoryOperationResult> PrepareClearAsync(string userId, Guid? operationId, CancellationToken cancellationToken)
    {
        var id = operationId ?? Guid.NewGuid();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.MemoryOperations.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (existing is not null) return ExistingResult(existing, userId);
        if (!_enabled) return new(id, "rejected", "memory_disabled");

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        db.MemoryOperations.Add(new MemoryOperation
        {
            Id = id,
            UserId = userId,
            Operation = "clear",
            ArgumentsHash = Hash("all"),
            Status = "awaiting_confirmation",
            ResultCode = "confirmation_required",
            ConfirmationTokenHash = Hash(token),
            ConfirmationExpiresAt = DateTime.UtcNow.Add(ClearConfirmationLifetime)
        });
        await db.SaveChangesAsync(cancellationToken);
        return new(id, "awaiting_confirmation", "confirmation_required", ConfirmationToken: token,
            ConfirmationExpiresAt: DateTime.UtcNow.Add(ClearConfirmationLifetime));
    }

    public async Task<MemoryOperationResult> ClearAsync(string userId, Guid operationId, string token, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var operation = await db.MemoryOperations.SingleOrDefaultAsync(item => item.Id == operationId && item.UserId == userId, cancellationToken);
        if (operation is null) return new(operationId, "rejected", "confirmation_not_found");
        if (operation.Status == "completed") return ExistingResult(operation, userId);
        if (operation.Status != "awaiting_confirmation" || operation.ConfirmationExpiresAt <= DateTime.UtcNow ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(operation.ConfirmationTokenHash ?? ""), Encoding.UTF8.GetBytes(Hash(token))))
            return new(operationId, "rejected", "confirmation_invalid");

        var now = DateTime.UtcNow;
        var items = await db.MemoryItems.Where(item => item.UserId == userId && item.Status == "active").ToListAsync(cancellationToken);
        foreach (var item in items)
        {
            item.Status = "tombstoned";
            item.TombstonedAt = now;
            item.UpdatedAt = now;
            item.Revision++;
            item.VisualAssetId = null;
        }
        operation.ConfirmationTokenHash = null;
        var result = Complete(operation, "completed", "cleared");
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task StorePassiveAsync(string userId, int sourceMessageId, string text, CancellationToken cancellationToken)
    {
        // Passive extraction has no user-facing receipt and never overwrites an
        // existing explicit item. It still writes before attempting embeddings.
        var normalized = NormalizeText(text);
        if (!_enabled || normalized.Length == 0 || LooksSensitive(normalized)) return;
        var hash = LongTermMemoryService.ComputeContentHash(normalized);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (await db.MemoryItems.AnyAsync(item => item.UserId == userId && item.ContentHash == hash, cancellationToken)) return;
        var item = new MemoryItem
        {
            UserId = userId,
            Text = normalized,
            ContentHash = hash,
            SourceMessageId = sourceMessageId,
            Category = MemoryCategories.Other
        };
        db.MemoryItems.Add(item);
        await db.SaveChangesAsync(cancellationToken);
        await PopulateEmbeddingAsync(item.Id, userId, cancellationToken);
    }

    private async Task<MemoryOperationResult> MutateAsync(
        string userId, string operationName, Guid? suppliedId, string arguments, Func<AppDbContext, MemoryOperation, Task<MemoryOperationResult>> execute, CancellationToken cancellationToken)
    {
        var id = suppliedId ?? Guid.NewGuid();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.MemoryOperations.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        var argumentsHash = Hash(arguments);
        if (existing is not null)
        {
            return existing.UserId == userId && existing.Operation == operationName && existing.ArgumentsHash == argumentsHash
                ? ExistingResult(existing, userId)
                : new(id, "rejected", "idempotency_conflict");
        }
        var operation = new MemoryOperation { Id = id, UserId = userId, Operation = operationName, ArgumentsHash = argumentsHash };
        db.MemoryOperations.Add(operation);
        await db.SaveChangesAsync(cancellationToken);
        var result = await execute(db, operation);
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    private async Task PopulateEmbeddingAsync(Guid itemId, string userId, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            if (!db.Database.IsNpgsql()) return;
            var item = await db.MemoryItems.SingleOrDefaultAsync(candidate => candidate.Id == itemId && candidate.UserId == userId, cancellationToken);
            if (item is null || item.Status != "active") return;
            var apiKey = await db.UserSettings.Where(settings => settings.UserId == userId)
                .Select(settings => settings.GeminiApiKey).FirstOrDefaultAsync(cancellationToken);
            item.Embedding = new Vector(await _gemini.GenerateEmbeddingAsync($"title: personal memory | text: {item.Text}", apiKey, cancellationToken));
            item.EmbeddingModel = GeminiService.EmbeddingModel;
            item.EmbeddingState = "ready";
            item.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Memory embedding is pending for item {MemoryItemId}", itemId);
        }
    }

    private static MemoryOperationResult Complete(MemoryOperation operation, string status, string code, Guid? memoryItemId = null)
    {
        operation.Status = status;
        operation.ResultCode = code;
        operation.MemoryItemId = memoryItemId ?? operation.MemoryItemId;
        operation.CompletedAt = DateTime.UtcNow;
        return new(operation.Id, status, code, operation.MemoryItemId);
    }

    private static MemoryOperationResult ExistingResult(MemoryOperation operation, string userId) =>
        operation.UserId == userId
            ? new(operation.Id, operation.Status, operation.ResultCode, operation.MemoryItemId)
            : new(operation.Id, "rejected", "idempotency_conflict");

    private static MemoryItemResponse ToResponse(MemoryItem item) => new(
        item.Id,
        item.Text,
        item.Kind,
        item.Category,
        item.Revision,
        item.Status,
        item.CreatedAt,
        item.UpdatedAt,
        item.LastRecalledAt,
        item.EmbeddingState,
        item.VisualAsset is null
            ? null
            : new MemoryAttachmentResponse(
                item.VisualAsset.Id,
                item.VisualAsset.MimeType,
                item.VisualAsset.SizeBytes,
                item.VisualAsset.OriginalFileName));

    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= MaxTextLength ? normalized : normalized[..MaxTextLength];
    }

    private static bool LooksSensitive(string text)
    {
        var lower = text.ToLowerInvariant();
        return lower.Contains("парол") || lower.Contains("код из смс") || lower.Contains("банковск") ||
               lower.Contains("диагноз") || lower.Contains("лекарств") || lower.Contains("медицин");
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
