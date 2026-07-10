using VoiceAssistant.API.Data;
using VoiceAssistant.API.Data.Entities;

namespace VoiceAssistant.API.Services;

public class ConversationMemoryService
{
    public const int SummarizationThresholdChars = 200_000;
    public const int CompactionThresholdChars = 100_000;

    private readonly AppDbContext _db;
    private readonly GeminiService _gemini;
    private readonly ILogger<ConversationMemoryService> _logger;

    public ConversationMemoryService(AppDbContext db, GeminiService gemini, ILogger<ConversationMemoryService> logger)
    {
        _db = db;
        _gemini = gemini;
        _logger = logger;
    }

    public static int UnsummarizedCharCount(IReadOnlyList<Message> messages, int? lastSummarizedMessageId)
    {
        var cursor = lastSummarizedMessageId ?? 0;
        return messages.Where(m => m.Id > cursor).Sum(m => m.Content.Length);
    }

    public static bool ShouldSummarize(IReadOnlyList<Message> messages, int? lastSummarizedMessageId)
        => UnsummarizedCharCount(messages, lastSummarizedMessageId) >= SummarizationThresholdChars;

    public static bool ShouldCompact(string? mediumTermSummary)
        => (mediumTermSummary?.Length ?? 0) > CompactionThresholdChars;
}
