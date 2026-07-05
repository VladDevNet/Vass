using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VoiceAssistant.API.Data;

namespace VoiceAssistant.API.Services;

public record SpeakerIdResult(string? KnownName, bool ShouldAskForName, bool JustRegistered);

public class SpeakerRegistryService
{
    // Tuned against synthetic-voice test clips: same speaker ~0.82, different
    // speakers ~0.16 cosine similarity — plenty of margin either side of 0.65.
    private const float MatchThreshold = 0.65f;
    private const int ConfirmCount = 3; // turns of consistent unknown voice before treating it as a real new speaker

    private readonly AppDbContext _db;
    private readonly SpeakerIdService _speakerId;
    private readonly SpeakerPendingStore _pending;
    private readonly GeminiService _gemini;
    private readonly ILogger<SpeakerRegistryService> _logger;

    public SpeakerRegistryService(AppDbContext db, SpeakerIdService speakerId, SpeakerPendingStore pending,
        GeminiService gemini, ILogger<SpeakerRegistryService> logger)
    {
        _db = db;
        _speakerId = speakerId;
        _pending = pending;
        _gemini = gemini;
        _logger = logger;
    }

    public async Task<SpeakerIdResult> IdentifyAsync(string wavPath, string transcript, string? geminiKey)
    {
        var embedding = await _speakerId.GetEmbeddingAsync(wavPath);
        if (embedding == null)
        {
            return new SpeakerIdResult(null, false, false);
        }

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

        // Not a known voice — is it the same unknown voice we've been tracking, or a different one?
        var (pendingEmbeddings, _) = _pending.Snapshot();
        if (pendingEmbeddings.Count > 0)
        {
            var clusterScore = SpeakerIdService.CosineSimilarity(embedding, Centroid(pendingEmbeddings));
            if (clusterScore < MatchThreshold)
            {
                _logger.LogInformation("SpeakerRegistry: new unknown voice differs from pending cluster, restarting.");
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
