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

    // Вызывается из ChatController.Send() после сохранения ОБЕИХ реплик хода.
    // Никогда не бросает — упавший side-call оставляет MediumTermSummary/
    // LastSummarizedMessageId нетронутыми, следующий ход попробует снова.
    public async Task CheckAndUpdateAsync(ChatSession session, string? geminiApiKey, CancellationToken ct)
    {
        try
        {
            var messages = session.Messages.ToList();
            if (ShouldSummarize(messages, session.LastSummarizedMessageId))
            {
                await SummarizeNewMessagesAsync(session, messages, geminiApiKey, ct);
            }

            if (ShouldCompact(session.MediumTermSummary))
            {
                await CompactSummaryAsync(session, geminiApiKey, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Клиент отключился — ничего ещё не сохранено, следующий ход попробует снова.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Medium-term memory update failed for session {SessionId}", session.Id);
        }
    }

    private async Task SummarizeNewMessagesAsync(ChatSession session, List<Message> messages, string? geminiApiKey, CancellationToken ct)
    {
        var cursor = session.LastSummarizedMessageId ?? 0;
        var newMessages = messages.Where(m => m.Id > cursor).OrderBy(m => m.Id).ToList();

        var transcript = string.Join("\n", newMessages.Select(m =>
            $"{(m.Role == "user" ? "Пользователь" : "Ассистент")}: {m.Content}"));

        var prompt = $$"""
            Ниже — фрагмент разговора пользователя с голосовым ассистентом-компаньоном.
            Составь короткую сводку (2000-4000 символов) главных фактов и идей из этого
            фрагмента — то, что стоит помнить в дальнейшем разговоре. Пиши по-русски,
            связным текстом, без markdown-разметки и заголовков.

            {{transcript}}
            """;

        var geminiMessages = new List<GeminiMessage> { new("user", prompt) };
        var sb = new System.Text.StringBuilder();
        await foreach (var chunk in _gemini.StreamResponseAsync("", geminiMessages, model: "gemini-3.5-flash",
            maxTokens: 2000, apiKey: geminiApiKey, enableGrounding: false, cancellationToken: ct))
        {
            sb.Append(chunk);
        }

        var newChunk = sb.ToString().Trim();
        if (string.IsNullOrEmpty(newChunk))
        {
            _logger.LogWarning("Medium-term summarization side-call returned no usable content for session {SessionId}", session.Id);
            return;
        }

        session.MediumTermSummary = string.IsNullOrEmpty(session.MediumTermSummary)
            ? newChunk
            : session.MediumTermSummary + "\n\n" + newChunk;
        session.LastSummarizedMessageId = newMessages[^1].Id;

        await _db.SaveChangesAsync();
        _logger.LogInformation("Medium-term summary extended for session {SessionId}, cursor now {MessageId}",
            session.Id, session.LastSummarizedMessageId);
    }

    private async Task CompactSummaryAsync(ChatSession session, string? geminiApiKey, CancellationToken ct)
    {
        var prompt = $$"""
            Ниже — накопленная сводка более ранней части разговора с пользователем. Она
            стала слишком длинной. Пересобери её заново, сохранив главные факты и идеи,
            но уложись примерно в 10000 символов. Пиши по-русски, связным текстом, без
            markdown-разметки и заголовков.

            {{session.MediumTermSummary}}
            """;

        var geminiMessages = new List<GeminiMessage> { new("user", prompt) };
        var sb = new System.Text.StringBuilder();
        await foreach (var chunk in _gemini.StreamResponseAsync("", geminiMessages, model: "gemini-3.5-flash",
            maxTokens: 4000, apiKey: geminiApiKey, enableGrounding: false, cancellationToken: ct))
        {
            sb.Append(chunk);
        }

        var compacted = sb.ToString().Trim();
        if (string.IsNullOrEmpty(compacted))
        {
            _logger.LogWarning("Medium-term compaction side-call returned no usable content for session {SessionId}", session.Id);
            return;
        }

        session.MediumTermSummary = compacted;
        await _db.SaveChangesAsync();
        _logger.LogInformation("Medium-term summary compacted for session {SessionId}, new length {Length}",
            session.Id, compacted.Length);
    }
}
