using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VoiceAssistant.API.Data;

namespace VoiceAssistant.API.Services;

public record SpeakerIdResult(string? KnownName, bool ShouldAskForName, bool JustRegistered);

// PROJECT-AUDIT-2026-07-10 SEC-02: SpeakerProfile has no UserId and
// SpeakerPendingStore is a process-wide singleton — IdentifyAsync below
// scans and clusters across ALL users' voices with no tenant isolation
// at all. Safe today only because it's gated behind Features:
// SpeakerIdentificationEnabled, which defaults to false when absent from
// config. Do NOT flip that flag on without first adding a UserId/household
// scope to SpeakerProfile, a composite index, filtering every query in
// this file by it, and making SpeakerPendingStore per-user instead of
// global — enabling as-is would mix names/voices/transcripts across
// unrelated accounts.
public class SpeakerRegistryService
{
    // NOTE: real short conversational clips (phone mic, varying distance) score
    // much lower on same-speaker similarity than clean synthetic voices did in
    // initial testing (0.82) — observed ~0.18-0.28 even for the same person under
    // imperfect conditions. This threshold is a rough starting point, not a
    // confidently-calibrated value; expect to retune once we have more real data.
    private const float MatchThreshold = 0.3f;
    private const int ConfirmCount = 3; // turns of consistent unknown voice before treating it as a real new speaker

    private readonly AppDbContext _db;
    private readonly SpeakerIdService _speakerId;
    private readonly SpeakerPendingStore _pending;
    private readonly GeminiService _gemini;
    private readonly ILogger<SpeakerRegistryService> _logger;
    private readonly bool _enabled;

    public SpeakerRegistryService(AppDbContext db, SpeakerIdService speakerId, SpeakerPendingStore pending,
        GeminiService gemini, IConfiguration config, ILogger<SpeakerRegistryService> logger)
    {
        _db = db;
        _speakerId = speakerId;
        _pending = pending;
        _gemini = gemini;
        _logger = logger;
        _enabled = config.GetValue("Features:SpeakerIdentificationEnabled", false);
    }

    public async Task<SpeakerIdResult> IdentifyAsync(string wavPath, string transcript, string? geminiKey)
    {
        if (!_enabled)
        {
            return new SpeakerIdResult(null, false, false);
        }

        var result = await _speakerId.GetEmbeddingAsync(wavPath);
        if (result == null)
        {
            return new SpeakerIdResult(null, false, false);
        }

        if (result.LowConfidence)
        {
            // Too quiet/short/noisy to trust for identification — neither match nor
            // let it corrupt whatever pending cluster we're building. Just skip it.
            _logger.LogInformation("SpeakerRegistry: skipping low-confidence clip.");
            return new SpeakerIdResult(null, false, false);
        }
        var embedding = result.Vector;

        var knownProfiles = await _db.SpeakerProfiles.ToListAsync();
        string? bestName = null;
        var bestScore = 0f;
        foreach (var profile in knownProfiles)
        {
            var profileEmbedding = JsonSerializer.Deserialize<float[]>(profile.EmbeddingJson)!;
            var score = SpeakerIdService.CosineSimilarity(embedding, profileEmbedding);
            if (score > bestScore)
            {
                bestScore = score;
                bestName = profile.Name;
            }
        }

        if (bestScore >= MatchThreshold)
        {
            _pending.Reset(); // a recognized speaker took a turn — any unrelated pending cluster is stale
            return new SpeakerIdResult(bestName, false, false);
        }

        // Not a known voice — is it the same unknown voice we've been tracking, or a
        // different one? Compare against the best-matching individual pending sample
        // (not the centroid) so one noisy prior sample doesn't drag the average down
        // and wrongly reset a cluster that's actually the same person.
        var (pendingEmbeddings, _) = _pending.Snapshot();
        if (pendingEmbeddings.Count > 0)
        {
            var bestClusterScore = pendingEmbeddings.Max(e => SpeakerIdService.CosineSimilarity(embedding, e));
            if (bestClusterScore < MatchThreshold)
            {
                _logger.LogInformation("SpeakerRegistry: new unknown voice differs from pending cluster (best={Score:F3}), restarting.", bestClusterScore);
                _pending.Reset();
            }
        }
        _pending.Add(embedding, transcript);

        if (_pending.Count < ConfirmCount)
        {
            return new SpeakerIdResult(null, false, false);
        }

        // Consistent unknown voice across several turns — confirmed as a real new speaker.
        var (finalEmbeddings, finalTranscripts) = _pending.Snapshot();
        var extractedName = await TryExtractNameAsync(finalTranscripts, geminiKey);

        if (extractedName != null)
        {
            _db.SpeakerProfiles.Add(new Data.Entities.SpeakerProfile
            {
                Name = extractedName,
                EmbeddingJson = JsonSerializer.Serialize(Centroid(finalEmbeddings))
            });
            await _db.SaveChangesAsync();
            _pending.Reset();
            _logger.LogInformation("SpeakerRegistry: registered new speaker '{Name}'.", extractedName);
            return new SpeakerIdResult(extractedName, false, true);
        }

        // No name surfaced naturally yet — keep tracking (don't reset) and let the
        // assistant work the question in on this turn.
        return new SpeakerIdResult(null, true, false);
    }

    private static float[] Centroid(List<float[]> embeddings)
    {
        var dims = embeddings[0].Length;
        var sum = new float[dims];
        foreach (var e in embeddings)
        {
            for (var i = 0; i < dims; i++) sum[i] += e[i];
        }
        for (var i = 0; i < dims; i++) sum[i] /= embeddings.Count;
        return sum;
    }

    private async Task<string?> TryExtractNameAsync(List<string> transcripts, string? geminiKey)
    {
        var combined = string.Join("\n", transcripts);
        var prompt = $$"""
            Вот несколько последних реплик человека, которого голосовой ассистент не узнаёт по голосу:
            ---
            {{combined}}
            ---
            Если человек представился по имени (например "привет, я Антон") ИЛИ кто-то другой явно обратился к нему по имени в этих репликах — ответь строго этим именем (одно слово, с большой буквы, в именительном падеже).
            Если имя не упоминается — ответь строго словом NONE.
            Ничего кроме имени или NONE не пиши.
            """;

        var messages = new List<GeminiMessage> { new("user", prompt) };
        var sb = new System.Text.StringBuilder();
        await foreach (var chunk in _gemini.StreamResponseAsync("Ты извлекаешь имена из текста.", messages, model: "gemini-3.5-flash", maxTokens: 20, apiKey: geminiKey))
        {
            sb.Append(chunk);
        }

        var result = sb.ToString().Trim().Trim('.', '!', '"');
        if (string.IsNullOrEmpty(result) || result.Equals("NONE", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return result;
    }
}
