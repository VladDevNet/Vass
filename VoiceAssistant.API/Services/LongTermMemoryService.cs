using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using VoiceAssistant.API.Data;
using VoiceAssistant.API.Data.Entities;

namespace VoiceAssistant.API.Services;

public class LongTermMemoryService
{
    public const int MaxExtractedFactsPerTurn = 3;
    public const int MaxRecalledFacts = 5;
    public const double MaxCosineDistance = 0.45;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly GeminiService _gemini;
    private readonly ILogger<LongTermMemoryService> _logger;
    private readonly bool _enabled;

    public LongTermMemoryService(
        IDbContextFactory<AppDbContext> dbFactory,
        GeminiService gemini,
        IConfiguration configuration,
        ILogger<LongTermMemoryService> logger)
    {
        _dbFactory = dbFactory;
        _gemini = gemini;
        _logger = logger;
        _enabled = configuration.GetValue("Features:LongTermMemoryEnabled", true);
    }

    public async Task<IReadOnlyList<string>> RecallAsync(
        string userId,
        string userMessage,
        string? geminiApiKey,
        CancellationToken cancellationToken)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(userMessage)) return [];

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (!db.Database.IsNpgsql()) return [];

        try
        {
            var hasMemories = await db.MemoryFacts.AnyAsync(memory =>
                memory.UserId == userId &&
                memory.IsActive &&
                memory.EmbeddingModel == GeminiService.EmbeddingModel,
                cancellationToken);
            if (!hasMemories) return [];

            var queryText = $"task: search result | query: {userMessage.Trim()}";
            var queryVector = new Vector(await _gemini.GenerateEmbeddingAsync(queryText, geminiApiKey, cancellationToken));

            var matches = await db.MemoryFacts
                .Where(memory => memory.UserId == userId &&
                                 memory.IsActive &&
                                 memory.EmbeddingModel == GeminiService.EmbeddingModel)
                .Select(memory => new
                {
                    Memory = memory,
                    Distance = memory.Embedding.CosineDistance(queryVector)
                })
                .Where(match => match.Distance <= MaxCosineDistance)
                .OrderBy(match => match.Distance)
                .ThenByDescending(match => match.Memory.UpdatedAt)
                .Take(MaxRecalledFacts)
                .ToListAsync(cancellationToken);

            if (matches.Count == 0) return [];

            var now = DateTime.UtcNow;
            foreach (var match in matches)
            {
                match.Memory.LastRecalledAt = now;
                match.Memory.RecallCount++;
            }
            await db.SaveChangesAsync(cancellationToken);

            return matches.Select(match => match.Memory.Fact).ToList();
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Long-term memory recall failed for user {UserId}", userId);
            return [];
        }
    }

    public async Task ExtractAndStoreAsync(
        string userId,
        int sourceMessageId,
        string userMessage,
        string? geminiApiKey,
        CancellationToken cancellationToken)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(userMessage)) return;

        await using (var capabilityCheck = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            if (!capabilityCheck.Database.IsNpgsql()) return;
        }

        try
        {
            var prompt = $$"""
                Извлеки из реплики пользователя только устойчивые личные факты, которые
                действительно пригодятся голосовому ассистенту в будущих разговорах:
                предпочтения, отношения и имена близких, важные бытовые детали, планы,
                привычки, местонахождение вещей. Не сохраняй вопрос, одноразовую просьбу,
                текущую тему разговора, инструкции ассистенту, пароли, платёжные данные,
                коды и номера документов. Не додумывай отсутствующее.

                Верни только JSON вида {"facts":["атомарный факт"]}. Максимум
                {{MaxExtractedFactsPerTurn}} коротких факта. Если сохранять нечего:
                {"facts":[]}.

                Реплика: {{userMessage}}
                """;

            var response = new StringBuilder();
            await foreach (var chunk in _gemini.StreamResponseAsync(
                               "",
                               [new GeminiMessage("user", prompt)],
                               model: "gemini-3.5-flash",
                               maxTokens: 600,
                               apiKey: geminiApiKey,
                               enableGrounding: false,
                               cancellationToken: cancellationToken))
            {
                response.Append(chunk);
            }

            var facts = ParseFacts(response.ToString());
            if (facts.Count == 0) return;

            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var storedCount = 0;
            foreach (var fact in facts)
            {
                var hash = ComputeContentHash(fact);
                if (await db.MemoryFacts.AnyAsync(m => m.UserId == userId && m.ContentHash == hash, cancellationToken))
                    continue;

                var documentText = $"title: personal memory | text: {fact}";
                var embedding = await _gemini.GenerateEmbeddingAsync(documentText, geminiApiKey, cancellationToken);
                db.MemoryFacts.Add(new MemoryFact
                {
                    UserId = userId,
                    Fact = fact,
                    ContentHash = hash,
                    Embedding = new Vector(embedding),
                    EmbeddingModel = GeminiService.EmbeddingModel,
                    SourceMessageId = sourceMessageId
                });
                storedCount++;
            }

            await db.SaveChangesAsync(cancellationToken);
            if (storedCount > 0)
                _logger.LogInformation("Stored {Count} long-term memory facts for user {UserId}", storedCount, userId);
        }
        catch (OperationCanceledException)
        {
            // The current turn can finish safely without a memory side effect.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Long-term memory extraction failed for user {UserId}", userId);
        }
    }

    public static IReadOnlyList<string> ParseFacts(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];

        var json = raw.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLineEnd = json.IndexOf('\n');
            var lastFence = json.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLineEnd >= 0 && lastFence > firstLineEnd)
                json = json[(firstLineEnd + 1)..lastFence].Trim();
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("facts", out var factsElement) ||
                factsElement.ValueKind != JsonValueKind.Array)
                return [];

            return factsElement.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => NormalizeFact(item.GetString()))
                .Where(fact => fact.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxExtractedFactsPerTurn)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string ComputeContentHash(string fact)
    {
        var normalized = NormalizeFact(fact).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static string NormalizeFact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 1000 ? normalized : normalized[..1000];
    }
}
