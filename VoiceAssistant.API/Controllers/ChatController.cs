using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VoiceAssistant.API.Data;
using VoiceAssistant.API.Data.Entities;
using VoiceAssistant.API.Services;

namespace VoiceAssistant.API.Controllers;

[ApiController]
[Route("api/v1/chat")]
[Authorize]
public class ChatController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly UserManager<User> _userManager;
    private readonly GeminiService _gemini;
    private readonly CompanionPromptService _tutor;
    private readonly AudioAnalysisService _audioAnalysis;
    private readonly SpeakerRegistryService _speakerRegistry;
    private readonly ConversationMemoryService _conversationMemory;
    private readonly LongTermMemoryService _longTermMemory;
    private readonly MemoryItemService _memoryItems;
    private readonly ReminderService _reminders;
    private readonly VisualAssetService _visualAssets;
    private readonly AssistantCapabilityRegistry _capabilities;
    private readonly ActionReceiptService _actionReceipts;
    private readonly AssistantAgentTurnService _agentTurn;
    private readonly IConfiguration _config;
    private readonly ILogger<ChatController> _logger;
    private readonly string _audioPath;

    private readonly PiperTtsService _ttsService;

    public ChatController(AppDbContext db, IDbContextFactory<AppDbContext> dbContextFactory, UserManager<User> userManager,
        GeminiService gemini, CompanionPromptService tutor,
        AudioAnalysisService audioAnalysis, SpeakerRegistryService speakerRegistry, ConversationMemoryService conversationMemory,
        LongTermMemoryService longTermMemory,
        MemoryItemService memoryItems,
        ReminderService reminders,
        VisualAssetService visualAssets,
        AssistantCapabilityRegistry capabilities,
        ActionReceiptService actionReceipts,
        AssistantAgentTurnService agentTurn,
        PiperTtsService ttsService,
        IConfiguration config, IWebHostEnvironment env, ILogger<ChatController> logger)
    {
        _db = db;
        _dbContextFactory = dbContextFactory;
        _userManager = userManager;
        _gemini = gemini;
        _tutor = tutor;
        _audioAnalysis = audioAnalysis;
        _speakerRegistry = speakerRegistry;
        _conversationMemory = conversationMemory;
        _longTermMemory = longTermMemory;
        _memoryItems = memoryItems;
        _reminders = reminders;
        _visualAssets = visualAssets;
        _capabilities = capabilities;
        _actionReceipts = actionReceipts;
        _agentTurn = agentTurn;
        _ttsService = ttsService;
        _config = config;
        _logger = logger;

        _audioPath = ResolveAudioPath(config["Audio:Path"], env.ContentRootPath);
    }

    // Static and parameterized so Program.cs's readiness check (REL-03) can
    // resolve the SAME directory this controller actually writes to/reads
    // from, without duplicating the resolution logic.
    public static string ResolveAudioPath(string? configuredPath, string contentRootPath)
    {
        var audioPath = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(contentRootPath, "audio")
            : configuredPath;
        return Path.GetFullPath(Path.IsPathRooted(audioPath)
            ? audioPath
            : Path.Combine(contentRootPath, audioPath));
    }

    // upload-audio (below) only ever generates "{guid}.webm" — /chat/send's
    // AudioFileName is a request-body string from an authenticated but
    // otherwise unverified caller, so it's treated as untrusted input, not a
    // free-form path. Format is checked first (structurally rules out ".."
    // and absolute paths on its own), then Path.GetFullPath + containment is
    // a second, independent layer, not a substitute (PROJECT-AUDIT-2026-07-10
    // SEC-04). Full ownership tracking (a real upload/attachment entity) is a
    // larger change left for later — this closes the path-traversal risk.
    // \A/\z (not ^/$) — $ alone still matches immediately before a single
    // trailing newline, which isn't exploitable here but is needlessly loose.
    private static readonly Regex SafeAudioFileNamePattern =
        new(@"\A[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.webm\z", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Transitional server-side command adapter. The model still does not
    // receive a free-form tool call; this recognizes only a direct user
    // request and returns a durable receipt before the model can confirm it.
    private static readonly Regex RememberCommandPattern = new(
        @"(?<!\p{L})(?:запомни(?:те|м)?|сохрани(?:те|м)?|запиши(?:те|м)?|добавь(?:те)?|внеси(?:те)?|положи(?:те|м)?|запомнить|сохранить|записать|добавить|внести|положить)\b(?<text>[^.!?\r\n]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex RememberLeadingFillerPattern = new(
        @"\A(?:(?:мне|нам|пожалуйста|давай(?:-ка)?|ну|вот|там|не\s+знаю)\s*,?\s*|(?:в\s+(?:(?:долгосрочную|обычную)\s+)?память)\s*,?\s*)+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex RememberTrailingMemoryPattern = new(
        @"\s+(?:в\s+(?:(?:долгосрочную|обычную)\s+)?память)\s*\z",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex RememberLinkReferencePattern = new(
        @"(?:ссылк\p{L}*|линк\p{L}*|виде\p{L}*|ролик\p{L}*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // A share target is commonly referenced in natural speech as just "ее",
    // "это" or "вот". Those words have no value as a memory on their own;
    // when a shared URL is available, they unambiguously point to that URL.
    private static readonly Regex RememberSharedReferenceOnlyPattern = new(
        @"\A(?:\s|,|(?:пожалуйста|давай(?:-ка)?|ну|вот|там|э+|м+|это|этот|эту|эти|его|её|ее|их))+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex UrlPattern = new(
        @"https?://[^\s<>()]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Static and parameterized on audioRootPath (not reading _audioPath
    // directly) so it's testable without constructing a full ChatController.
    public static bool TryResolveSafeAudioPath(string audioRootPath, string fileName, out string filePath)
    {
        filePath = "";
        if (!SafeAudioFileNamePattern.IsMatch(fileName)) return false;

        var candidate = Path.GetFullPath(Path.Combine(audioRootPath, fileName));
        var relative = Path.GetRelativePath(audioRootPath, candidate);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return false;

        filePath = candidate;
        return true;
    }

    private sealed record ExplicitMemoryCommandResult(MemoryOperationResult Receipt, string Text);

    private async Task<ExplicitMemoryCommandResult?> TryRememberCommandAsync(
        string userId, int sourceMessageId, string messageText, string? sharedContent,
        IEnumerable<Message> conversation, string? geminiApiKey, CancellationToken cancellationToken)
    {
        var text = TryGetExplicitMemoryTarget(
            messageText,
            sharedContent,
            conversation.Where(item => item.Role == "user").Select(item => item.Content));
        if (string.IsNullOrWhiteSpace(text)) return null;

        using var normalizationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        normalizationTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        var normalizedText = await _longTermMemory.NormalizeExplicitMemoryAsync(
            text, geminiApiKey, normalizationTimeout.Token);
        if (string.IsNullOrWhiteSpace(normalizedText)) normalizedText = text;

        var receipt = await _memoryItems.RememberAsync(
            userId,
            normalizedText,
            sourceMessageId,
            Guid.NewGuid(),
            cancellationToken);
        return new(receipt, normalizedText);
    }

    // Kept public as a narrow, deterministic test seam: this is deliberately
    // syntax/attachment routing, not a decision delegated to the chat model.
    // The model may refine the durable wording later, but it must never be
    // allowed to lose the URL that the user explicitly asked to retain.
    public static string? TryGetExplicitMemoryTarget(
        string messageText, string? sharedContent, IEnumerable<string?> previousUserMessages)
    {
        var match = RememberCommandPattern.Match(messageText);
        if (!match.Success) return null;

        var target = match.Groups["text"].Value.Trim(' ', ',', ':', '-');
        while (true)
        {
            var withoutFiller = RememberLeadingFillerPattern.Replace(target, "");
            if (withoutFiller == target) break;
            target = withoutFiller.Trim(' ', ',', ':', '-');
        }
        target = RememberTrailingMemoryPattern.Replace(target, "").Trim(' ', ',', ':', '-');
        if (target.StartsWith("что ", StringComparison.OrdinalIgnoreCase)) target = target[4..].Trim();

        var directUrl = GetLatestUrl(target);
        if (directUrl is not null) return FormatSavedLink(directUrl);

        var sharedUrl = GetLatestUrl(sharedContent) ?? GetLatestUrl(previousUserMessages);
        if (sharedUrl is not null &&
            (RememberLinkReferencePattern.IsMatch(target) || RememberSharedReferenceOnlyPattern.IsMatch(target)))
        {
            return FormatSavedLink(sharedUrl);
        }

        // Do not create a memory from a bare pronoun if there is no share
        // context to resolve it against. A normal chat reply is preferable to
        // silently persisting "ее, пожалуйста" as a supposed fact.
        return RememberSharedReferenceOnlyPattern.IsMatch(target) ? null : target;
    }

    private static string? GetLatestUrl(string? content)
    {
        var matches = UrlPattern.Matches(content ?? "");
        return matches.Count == 0 ? null : matches[^1].Value.TrimEnd('.', ',', ';', ':', ')', ']', '}');
    }

    private static string? GetLatestUrl(IEnumerable<string?> messages)
    {
        foreach (var message in messages.Reverse())
        {
            var url = GetLatestUrl(message);
            if (url is not null) return url;
        }
        return null;
    }

    private static string FormatSavedLink(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)))
        {
            return $"Сохраненная ссылка на YouTube: {url}";
        }

        return $"Сохраненная ссылка: {url}";
    }

    // Kept as a public compatibility seam for existing security tests and
    // OCR. Visual Capture uses the same byte-level inspector, so MIME rules
    // cannot silently diverge between the two image paths.
    public static bool TryDetectImageMimeType(byte[] content, out string mimeType) =>
        ImageContentInspector.TryDetectMimeType(content, out mimeType);

    // The Send action used to pass settings.DisplayName as GetSystemPrompt's
    // userName UNCONDITIONALLY, then separately PREPEND a second "Сейчас с
    // тобой говорит: X" note whenever speaker-id matched a known voice --
    // if the two ever disagreed (e.g. a family member sharing the device
    // via device-link, recognized by speaker-id under a name that isn't
    // the account's own DisplayName), the model saw two contradicting
    // "who you're talking to" claims in the same prompt. Speaker-id
    // reflects who is ACTUALLY speaking on THIS turn, so it takes priority
    // over the account-level DisplayName rather than stacking alongside it
    // -- there is only ever one userName slot now. Dormant while
    // Features:SpeakerIdentificationEnabled is off (SEC-02):
    // speakerKnownName is then always null and this always resolves to
    // displayName, unchanged from before.
    //
    // Re-review before ever flipping that flag on: SpeakerRegistryService
    // has no tenant isolation yet (matches globally across ALL accounts'
    // voices) at a match threshold its own comment calls "not
    // confidently-calibrated." A false cross-account match would now fully
    // REPLACE the correct DisplayName with a stranger's name in the
    // prompt, where the old (contradictory) code at least still mentioned
    // the true DisplayName alongside the wrong prepend. Worth raising the
    // override threshold or keeping DisplayName as a fallback mention on a
    // match, not just accepting KnownName outright, as part of that
    // future work -- not fixed here since it's inert while the flag is off.
    public static string? ResolvePromptUserName(string? speakerKnownName, string? displayName)
        => speakerKnownName ?? displayName;

    private static string GetExternalActionFallback(string actionType) => actionType switch
    {
        ExternalActionTypes.OpenVass => "Возвращаюсь в Vass.",
        ExternalActionTypes.YouTubeWatch => "Открываю выбранное видео в YouTube.",
        _ => "Открываю поиск в YouTube."
    };

    private static string GetMemoryReceiptFallback(MemoryOperationResult receipt, string? savedText = null) => receipt.Code switch
    {
        "remembered" when !string.IsNullOrWhiteSpace(savedText) => $"Сохранено в долгосрочную память: {savedText}.",
        "remembered" => "Сохранено в долгосрочную память.",
        "already_known" when !string.IsNullOrWhiteSpace(savedText) => $"Это уже сохранено в долгосрочной памяти: {savedText}.",
        "already_known" => "Это уже сохранено в долгосрочной памяти.",
        "memory_disabled" => "Долгосрочная память сейчас отключена, поэтому сохранить не удалось.",
        "sensitive_content_not_allowed" => "Такую чувствительную информацию я не сохраняю в долгосрочную память.",
        _ => "Не удалось подтвердить сохранение в долгосрочную память."
    };

    private const int MaxSharedContentLength = 20_000;

    private static string? NormalizeSharedContent(string? sharedContent)
    {
        if (string.IsNullOrWhiteSpace(sharedContent)) return null;
        var normalized = sharedContent.Trim();
        return normalized.Length <= MaxSharedContentLength
            ? normalized
            : normalized[..MaxSharedContentLength];
    }

    private static string IncludeSharedContent(string messageText, string? sharedContent) =>
        sharedContent is null
            ? messageText
            : $"{messageText}\n\nПользователь поделился следующим содержимым:\n{sharedContent}";

    public record SendRequest(
        int SessionId,
        string Message,
        string? AudioFileName = null,
        string? DeviceId = null,
        string? TimeZoneId = null,
        bool SupportsExternalActions = false,
        Guid? VisualAssetId = null,
        bool SupportsScreenAnalysis = false,
        string? SharedContent = null,
        int ReminderProtocolVersion = 1,
        Guid? ClientTurnId = null);

    private const long MaxAudioSize = 5 * 1024 * 1024; // 5MB
    private const long MaxImageSize = 10 * 1024 * 1024; // 10MB

    // [RequestSizeLimit] below bounds the WHOLE request body (multipart
    // boundary + Content-Disposition/Content-Type headers, not just the file
    // part) -- setting it to exactly MaxAudioSize rejects a legitimate file
    // AT that size, since the surrounding multipart overhead pushes the
    // total over the limit (confirmed empirically during SEC-07 review).
    // MaxAudioSize itself, checked against file.Length below, remains the
    // real enforced ceiling on file content.
    private const long MaxAudioRequestBodySize = MaxAudioSize + 64 * 1024;

    // TTS synthesizes whatever text it's given — an unbounded string is free
    // CPU-burning fuel for any authenticated caller (PROJECT-AUDIT-2026-07-10
    // SEC-07). 2000 chars is generous for a single spoken utterance (the
    // streaming path already sends one sentence at a time — see PR #55).
    private const int MaxTtsTextLength = 2000;

    // Human-readable chat session title, same class of field as
    // DisplayName/AssistantName below in SettingsController.
    private const int MaxSessionTitleLength = 200;

    // GetPreambleIfNeededAsync/MaybeUpdateCustomInstructionsAsync below only ever
    // need a short yes/no/short-phrase answer, not a full reply -- kept public
    // (not just small) because VoiceAssistant.API.IntegrationTests' FakeGeminiHandler
    // keys off these exact values to tell these background checks apart from a real
    // chat reply (which uses StreamResponseAsync's own maxTokens default instead);
    // a compile-time reference here means a future change to either number can't
    // silently desync from what the fake HTTP transport expects
    // (PROJECT-AUDIT-2026-07-10 QA-01).
    public const int PreambleCheckMaxTokens = 30;
    public const int CustomInstructionCheckMaxTokens = 200;

    public record TtsRequest(string Text, string? Voice = null);

    [HttpPost("tts")]
    public async Task<IActionResult> GenerateTts([FromBody] TtsRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Text)) return BadRequest();
        if (req.Text.Length > MaxTtsTextLength)
            return BadRequest(new { error = $"Text too long (max {MaxTtsTextLength} characters)" });

        var sw = Stopwatch.StartNew();
        var audioBytes = await _ttsService.GenerateSpeechAsync(req.Text);
        sw.Stop();
        _logger.LogInformation("VoiceAssistant Performance Stats - TTS: {TtsMs}ms for {Chars} chars", sw.ElapsedMilliseconds, req.Text.Length);

        if (audioBytes == null)
        {
            return BadRequest(new { error = "Neural TTS generation failed" });
        }

        return File(audioBytes, "audio/wav", "speech.wav");
    }

    // Streams raw 16-bit mono PCM (22050 Hz, little-endian) as it's synthesized,
    // instead of waiting for the whole utterance like /tts does.
    [HttpPost("tts_stream")]
    public async Task GenerateTtsStream([FromBody] TtsRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Text)) { Response.StatusCode = 400; return; }
        if (req.Text.Length > MaxTtsTextLength) { Response.StatusCode = 400; return; }

        Response.ContentType = "application/octet-stream";

        var sw = Stopwatch.StartNew();
        var ok = await _ttsService.StreamSpeechAsync(req.Text, Response.Body, HttpContext.RequestAborted);
        sw.Stop();
        _logger.LogInformation("VoiceAssistant Performance Stats - TTS (streamed): {TtsMs}ms for {Chars} chars", sw.ElapsedMilliseconds, req.Text.Length);

        if (!ok && !Response.HasStarted)
        {
            Response.StatusCode = 500;
        }
    }

    // Pure read -- PROJECT-AUDIT-2026-07-10 section 6 flagged this endpoint
    // creating a session as a side effect of a GET. The user's initial
    // session is now created once, at registration (AuthController.Register),
    // so this never needs to. Still defensive (empty array, not an error) in
    // case that invariant is ever violated -- HomeScreen.tsx already handles
    // a zero-length result.
    [HttpGet("sessions")]
    public async Task<IActionResult> GetSessions()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var session = await _db.ChatSessions
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new { s.Id, s.Mode, s.Title, s.CreatedAt })
            .FirstOrDefaultAsync();

        return Ok(session == null ? Array.Empty<object>() : [session]);
    }

    public record RenameSessionRequest(string Title);

    [HttpPatch("sessions/{id:int}")]
    public async Task<IActionResult> RenameSession(int id, [FromBody] RenameSessionRequest req)
    {
        if (req.Title.Length > MaxSessionTitleLength)
            return BadRequest(new { error = $"Заголовок слишком длинный (максимум {MaxSessionTitleLength} символов)" });

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var session = await _db.ChatSessions.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId);
        if (session == null) return NotFound();
        session.Title = req.Title;
        await _db.SaveChangesAsync();
        return Ok(new { session.Id, session.Title });
    }

    [HttpDelete("sessions/{id:int}")]
    public async Task<IActionResult> DeleteSession(int id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var session = await _db.ChatSessions
            .Include(s => s.Messages)
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId);

        if (session == null) return NotFound();

        var visualAssetIds = await _db.MessageAttachments
            .Where(attachment => attachment.Message.ChatSessionId == id)
            .Select(attachment => attachment.VisualAssetId)
            .Distinct()
            .ToListAsync(HttpContext.RequestAborted);

        foreach (var msg in session.Messages)
        {
            // Re-validated here too, not just at the one write site that
            // currently persists AudioFileName — this is the actual
            // File.Delete sink, so it shouldn't have to trust that every
            // past or future write path got the check right.
            if (!string.IsNullOrEmpty(msg.AudioFileName) &&
                TryResolveSafeAudioPath(_audioPath, msg.AudioFileName, out var filePath) &&
                System.IO.File.Exists(filePath))
            {
                System.IO.File.Delete(filePath);
            }
        }

        _db.ChatSessions.Remove(session);
        await _db.SaveChangesAsync();

        // An asset can be referenced by repeated/superseding attempts. Remove
        // only files whose last message link disappeared with this session.
        var orphanedVisualAssets = await _db.VisualAssets
            .Where(asset => visualAssetIds.Contains(asset.Id) && !asset.MessageAttachments.Any())
            .ToListAsync(HttpContext.RequestAborted);
        _db.VisualAssets.RemoveRange(orphanedVisualAssets);
        await _db.SaveChangesAsync(HttpContext.RequestAborted);
        foreach (var asset in orphanedVisualAssets)
        {
            try { await _visualAssets.DeleteIfExistsAsync(asset.StorageFileName); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete orphaned visual asset file after session deletion: AssetId={AssetId}", asset.Id);
            }
        }
        return NoContent();
    }

    // Paginated: `before` (a message Id) walks backward from the newest message
    // (omit for the first page), `limit` caps page size. A long-lived session
    // (one user already has 599+ messages) can't be sent whole on every open of
    // the history screen — the client renders newest-first and loads older
    // pages on demand as the user scrolls up.
    [HttpGet("sessions/{id:int}")]
    public async Task<IActionResult> GetSession(int id, [FromQuery] int? before = null, [FromQuery] int limit = 30)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var session = await _db.ChatSessions.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId);

        if (session == null) return NotFound();

        var query = _db.Messages.Where(m => m.ChatSessionId == id);
        if (before.HasValue)
        {
            query = query.Where(m => m.Id < before.Value);
        }

        var page = await query
            .Include(message => message.Attachments)
            .ThenInclude(attachment => attachment.VisualAsset)
            .OrderByDescending(m => m.Id)
            .Take(limit)
            .ToListAsync();
        page.Reverse(); // chronological order, matching the non-paginated response this replaces

        return Ok(new
        {
            session.Id,
            session.Mode,
            session.Title,
            Messages = page.Select(m => new
            {
                m.Id,
                m.Role,
                m.Content,
                m.CreatedAt,
                m.AudioFileName,
                Attachments = m.Attachments.Select(attachment => new
                {
                    id = attachment.VisualAssetId,
                    kind = attachment.Kind,
                    mimeType = attachment.VisualAsset.MimeType,
                    sizeBytes = attachment.VisualAsset.SizeBytes,
                    originalName = attachment.VisualAsset.OriginalFileName,
                })
            }),
            HasMore = page.Count == limit
        });
    }

    [HttpPost("send")]
    public async Task Send([FromBody] SendRequest req)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null) { Response.StatusCode = 401; return; }

        var session = await _db.ChatSessions
            .Include(s => s.Messages.OrderBy(m => m.CreatedAt))
            .FirstOrDefaultAsync(s => s.Id == req.SessionId && s.UserId == userId);

        if (session == null) { Response.StatusCode = 404; return; }

        // Load user settings for per-user API keys + custom prompt
        var settings = await _db.UserSettings.FirstOrDefaultAsync(s => s.UserId == userId);
        var geminiKey = settings?.GeminiApiKey;

        var messageText = req.Message;
        var sharedContent = NormalizeSharedContent(req.SharedContent);
        string? transcription = null;
        string? wavPath = null;
        long convertMs = 0;
        long transcribeMs = 0;
        long speakerIdMs = 0;
        SpeakerIdResult? speakerResult = null;
        // Tracks "did we enter the audio-transcription branch at all" —
        // deliberately NOT the same as "transcription != null" (see the
        // no_speech gate below). TranscribeAsync (AudioAnalysisService.cs)
        // returns null for EVERY failure mode along this path — missing API
        // key, a non-2xx Gemini response, a thrown exception, AND the
        // prompt-leak guard — not just for the one case (Gemini literally
        // complying with "return an empty string for silence") that yields
        // "". A gate on transcription != null would silently miss the
        // prompt-leak case specifically, which is the exact scenario a real
        // production 400 was traced back to.
        var attemptedTranscription = false;
        // Only ever set from a value that has already passed
        // TryResolveSafeAudioPath below — req.AudioFileName itself must never
        // reach Message.AudioFileName unvalidated, since DeleteSession later
        // does an unguarded Path.Combine + File.Delete on whatever is stored
        // there (PROJECT-AUDIT-2026-07-10 SEC-04 review, round 2: validating
        // only the transcription branch left this reachable by sending a
        // non-blank Message alongside an arbitrary AudioFileName).
        string? validatedAudioFileName = null;

        if (string.IsNullOrWhiteSpace(messageText) && !string.IsNullOrEmpty(req.AudioFileName))
        {
            attemptedTranscription = true;
            if (!TryResolveSafeAudioPath(_audioPath, req.AudioFileName, out var filePath) || !System.IO.File.Exists(filePath))
            {
                Response.StatusCode = 400;
                return;
            }
            validatedAudioFileName = req.AudioFileName;

            // Convert webm → wav (Gemini doesn't support webm)
            var sw = Stopwatch.StartNew();
            wavPath = await ConvertToWavAsync(filePath);
            sw.Stop();
            convertMs = sw.ElapsedMilliseconds;

            if (wavPath == null)
            {
                _logger.LogWarning("ffmpeg conversion failed for {File}", filePath);
                Response.StatusCode = 400;
                await Response.WriteAsJsonAsync(new { error = "no_speech" });
                return;
            }

            sw.Restart();
            transcription = await _audioAnalysis.TranscribeAsync(wavPath, geminiKey);
            sw.Stop();
            transcribeMs = sw.ElapsedMilliseconds;

            messageText = transcription;

            // Disabled by default via Features:SpeakerIdentificationEnabled (see
            // SpeakerRegistryService — PROJECT-AUDIT-2026-07-10 SEC-02, no tenant
            // isolation yet) — IdentifyAsync no-ops immediately when that's unset,
            // so this call is always cheap while off. Independently ALSO not worth
            // enabling yet even once isolation is added: real short phone-mic clips
            // scored too close to noise-floor similarity in testing (~0.18-0.28,
            // near the ~0.16 seen between different synthetic voices) to trust, at
            // 400ms-2s cost per turn for an unproven payoff.
            if (!string.IsNullOrWhiteSpace(transcription))
            {
                sw.Restart();
                speakerResult = await _speakerRegistry.IdentifyAsync(wavPath, transcription, geminiKey);
                sw.Stop();
                speakerIdMs = sw.ElapsedMilliseconds;
            }
        }

        // Keep the spoken/typed command apart from the share payload until
        // explicit-memory routing has identified its target. Once merged, a
        // regex can mistake the trailing share text for the command itself.
        var commandText = messageText;
        messageText = IncludeSharedContent(messageText ?? "", sharedContent);

        if (string.IsNullOrWhiteSpace(messageText))
        {
            Response.StatusCode = 400;
            if (attemptedTranscription)
            {
                // Audio was captured and transcription was attempted, but
                // nothing usable came back — genuine silence/noise, a
                // missing API key, a failed Gemini call, or
                // AudioAnalysisService's own anti-leak/anti-hallucination
                // guards (PR #43/#46) neutralizing a bad read — ALL of
                // TranscribeAsync's failure paths return null, not just the
                // "Gemini literally complied with 'return empty string for
                // silence'" case, so this gate gets confused with
                // transcription != null (a real bug caught by review before
                // this shipped: that gate silently missed the prompt-leak
                // case specifically — the exact scenario a production 400
                // was traced back to). Whatever the underlying reason, it's
                // a normal, expected outcome in a live conversation, not a
                // real error — distinct from a malformed request (this flag
                // stays false if the audio-transcription branch above was
                // never entered at all, e.g. neither message nor audio
                // provided).
                await Response.WriteAsJsonAsync(new { error = "no_speech" });
            }
            return;
        }

        VisualAsset? visualAsset = null;
        byte[]? visualContent = null;
        if (req.VisualAssetId is { } visualAssetId)
        {
            visualAsset = await _db.VisualAssets
                .FirstOrDefaultAsync(asset => asset.Id == visualAssetId && asset.UserId == userId, HttpContext.RequestAborted);
            if (visualAsset is null)
            {
                Response.StatusCode = 400;
                await Response.WriteAsJsonAsync(new { error = "Вложение недоступно. Выберите его еще раз." });
                return;
            }

            visualContent = await _visualAssets.ReadAsync(visualAsset.StorageFileName, HttpContext.RequestAborted);
            if (visualContent is null || visualContent.Length == 0 || visualContent.Length > ImageContentInspector.MaxAttachmentSize ||
                !ImageContentInspector.TryNormalizeAttachmentMimeType(visualAsset.MimeType, out _))
            {
                Response.StatusCode = 400;
                await Response.WriteAsJsonAsync(new { error = "Не удалось прочитать прикрепленное вложение." });
                return;
            }
        }

        // Persist a content-free record of the runtime capabilities the model
        // sees for this turn. It makes a later diagnosis reproducible without
        // logging the prompt, user text, attachment, or provider payload.
        var supportsReminderDevice = ReminderService.IsValidDeviceId(req.DeviceId) &&
                                     ReminderService.TryResolveTimeZone(req.TimeZoneId, out _);
        var capabilityContext = new AssistantRuntimeContext(
            HasVisualAttachment: visualAsset is not null,
            SupportsScreenAnalysis: req.SupportsScreenAnalysis,
            SupportsExternalActions: req.SupportsExternalActions,
            SupportsReminders: supportsReminderDevice,
            DeviceId: req.DeviceId,
            TimeZoneId: req.TimeZoneId,
            SupportsPeriodicReminders: supportsReminderDevice && req.ReminderProtocolVersion >= 2,
            ClientTurnId: req.ClientTurnId);
        var capabilitySnapshot = _capabilities.GetSnapshot(capabilityContext);

        // Save user message
        var userMessage = new Message
        {
            Role = "user",
            Content = messageText,
            AudioFileName = validatedAudioFileName,
            CapabilitySnapshotJson = AssistantCapabilityRegistry.SerializeContentFreeSnapshot(capabilitySnapshot)
        };
        if (visualAsset is not null)
        {
            userMessage.Attachments.Add(new MessageAttachment
            {
                VisualAssetId = visualAsset.Id,
                Kind = ImageContentInspector.IsImageMimeType(visualAsset.MimeType) ? "image" : "document",
            });
        }
        session.Messages.Add(userMessage);
        await _db.SaveChangesAsync();

        // Check (in parallel, doesn't block the response) whether the user asked the
        // assistant to remember a persistent behavior preference ("говори медленнее",
        // "давай на ты", etc.) so future turns' system prompts reflect it. Awaited at
        // the very end of this action — the client already moves on once it sees
        // [DONE], so this adds no perceived latency. Genuinely runs concurrently with
        // the rest of this method (including the _db.SaveChangesAsync below for the
        // assistant message) until awaited, so it uses its own DbContext instance
        // internally rather than the shared _db (PROJECT-AUDIT-2026-07-10 REL-01).
        var instructionUpdateTask = MaybeUpdateCustomInstructionsAsync(userId, messageText, settings?.CustomSystemPrompt, geminiKey, HttpContext.RequestAborted);

        // Semantic recall is the only long-term-memory operation on the critical
        // path. Explicit agent memory calls below suppress passive extraction.
        var recalledFacts = await _longTermMemory.RecallAsync(userId, messageText, geminiKey, HttpContext.RequestAborted);

        // Build conversation history: this session is persistent and never rotates
        // (single ongoing session per user), so resending every message ever exchanged
        // would grow unbounded — already 294 messages deep for one user. Cap it to a
        // recent window: take at most the last MaxCandidateMessages, then walk
        // backwards from the newest adding messages until HistoryCharBudget is hit
        // (~4 chars/token, so ~6000 tokens of actual history — not an exact tokenizer,
        // just enough to keep latency/cost bounded without losing recent context).
        const int MaxCandidateMessages = 100;
        const int HistoryCharBudget = 24000;

        var windowed = new List<Message>();
        var usedChars = 0;
        foreach (var m in session.Messages
                     .Where(item => !string.IsNullOrWhiteSpace(item.Content))
                     .OrderByDescending(m => m.CreatedAt)
                     .Take(MaxCandidateMessages))
        {
            if (windowed.Count > 0 && usedChars + m.Content.Length > HistoryCharBudget)
            {
                break;
            }
            windowed.Add(m);
            usedChars += m.Content.Length;
        }
        windowed.Reverse(); // back to chronological order for the API

        var messages = windowed.Select(m =>
        {
            var role = m.Role == "user" ? "user" : "assistant";
            return m.Id == userMessage.Id && visualAsset is not null && visualContent is not null
                ? new GeminiMessage(role,
                [
                    new GeminiPart(Text: m.Content),
                    new GeminiPart(MimeType: visualAsset.MimeType, Data: visualContent),
                ])
                : new GeminiMessage(role, m.Content);
        }).ToList();

        var systemPrompt = _tutor.GetSystemPrompt(
            settings?.CustomSystemPrompt,
            ResolvePromptUserName(speakerResult?.KnownName, settings?.DisplayName),
            settings?.AssistantName,
            session.MediumTermSummary,
            recalledFacts);

        systemPrompt = _capabilities.BuildPromptManifest(capabilityContext) + "\n\n" + systemPrompt;

        if (visualAsset is not null)
        {
            var attachmentDescription = ImageContentInspector.IsImageMimeType(visualAsset.MimeType)
                ? "изображение"
                : "файл";
            systemPrompt = $"Пользователь приложил {attachmentDescription} к текущей реплике. Отвечай на его конкретную просьбу с учетом вложения. " +
                           "Если нужных данных в нем нет или формат не читается, честно скажи об этом и задай один короткий уточняющий вопрос. " +
                           "Не распознавай личность по лицу и не выдавай медицинские, юридические или финансовые выводы за достоверные.\n\n" +
                           systemPrompt;
        }

        if (ReminderService.TryResolveTimeZone(req.TimeZoneId, out var localTimeZone))
        {
            var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, localTimeZone);
            systemPrompt = $"Текущее локальное время пользователя: {nowLocal:yyyy-MM-ddTHH:mm:ss}; " +
                           $"часовой пояс: {localTimeZone.Id}. Для относительной даты в истории вычисляй точные local dates и передавай их в conversation_search.\n\n" +
                           systemPrompt;

            if (capabilityContext.SupportsReminders)
            {
                systemPrompt = "Для одного срабатывания вызывай reminder_create с точным будущим временем. " +
                               (capabilityContext.SupportsPeriodicReminders
                                   ? "Для повторения вызывай только periodic_reminder_create: передай ближайший точный startAtLocal и поддерживаемый RRULE; при неоднозначности или неподдерживаемом RRULE сначала задай один короткий уточняющий вопрос. "
                                   : "Периодические напоминания этот клиент пока не поддерживает; не вызывай periodic_reminder_create и честно сообщи ограничение. ") +
                               "Не создавай больше одного напоминания за ход.\n\n" +
                               systemPrompt;
            }
        }

        if (speakerResult?.ShouldAskForName == true)
        {
            systemPrompt = $"Ты слышишь новый, ранее не знакомый тебе голос несколько реплик подряд. Ненавязчиво, как естественную часть своего ответа по теме разговора, поинтересуйся как зовут собеседника — не делай это отдельным резким вопросом или объявлением о распознавании голоса.\n\n{systemPrompt}";
        }

        var agentTurn = await _agentTurn.RunAsync(
            systemPrompt,
            messages,
            geminiKey,
            userId,
            userMessage.Id,
            capabilityContext,
            HttpContext.RequestAborted);
        var toolExecutions = agentTurn.ToolExecutions;
        var screenCaptureRequested = agentTurn.RequestsScreenCapture;
        var reminderDraft = toolExecutions.Select(result => result.Reminder).FirstOrDefault(reminder => reminder is not null);
        var attemptedReminder = toolExecutions.Any(result =>
            result.Name is "reminder_create" or "periodic_reminder_create");
        var actionExecution = toolExecutions.FirstOrDefault(result => result.ExternalAction is not null && result.ActionReceiptId is not null);
        var externalAction = actionExecution?.ExternalAction;
        var actionProposal = actionExecution is null || actionExecution.ActionReceiptId is not { } actionId || externalAction is null
            ? null
            : new ActionProposal(
                actionId,
                externalAction.Type,
                ActionReceiptService.GetTaxonomy(externalAction.Type) ?? AssistantActionTaxonomies.External,
                externalAction.Query,
                externalAction.VideoId);

        // A normal stream is only a resilience fallback after a tool turn did
        // not produce a final provider reply. It receives verified receipts,
        // never an untrusted model claim about side effects.
        if (agentTurn.UsedTools && string.IsNullOrWhiteSpace(agentTurn.FinalText) && toolExecutions.Count > 0)
        {
            var verifiedResults = string.Join("\n", toolExecutions.Select(result => $"- {result.Name}: {result.Summary}"));
            systemPrompt = $"Инструменты уже выполнились и вернули только следующие подтвержденные результаты:\n{verifiedResults}\n" +
                           "Не вызывай и не обещай новых действий. Сформулируй короткий ответ строго по этим результатам.\n\n" + systemPrompt;
        }
        else if (attemptedReminder && reminderDraft is null)
        {
            var reminderResults = string.Join("\n", toolExecutions
                .Where(result => result.Name is "reminder_create" or "periodic_reminder_create")
                .Select(result => $"- {result.Name}: {result.Summary}"));
            systemPrompt = $"Напоминание не было создано. Проверенные результаты:\n{reminderResults}\n" +
                           "Не утверждай, что оно установлено; кратко объясни ограничение или попроси недостающее уточнение.\n\n" +
                           systemPrompt;
        }

        // SSE streaming response
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        // Send transcription event if audio was transcribed
        if (transcription != null)
        {
            var trData = JsonSerializer.Serialize(new { transcription });
            await Response.WriteAsync($"data: {trData}\n\n");
            await Response.Body.FlushAsync();
        }

        if (actionProposal is not null)
        {
            var actionData = JsonSerializer.Serialize(new
            {
                externalAction = new
                {
                    actionId = actionProposal.ActionId,
                    type = actionProposal.Type,
                    taxonomy = actionProposal.Taxonomy,
                    query = actionProposal.Query,
                    videoId = actionProposal.VideoId
                }
            });
            await Response.WriteAsync($"data: {actionData}\n\n");
            await Response.Body.FlushAsync();
        }

        if (screenCaptureRequested)
        {
            // This was an orchestration preflight. The retry with the actual
            // image owns the user message, just like the existing capture
            // flow above; do not leave a duplicate text-only turn behind.
            _db.Messages.Remove(userMessage);
            await _db.SaveChangesAsync(HttpContext.RequestAborted);

            // The provisional turn is intentionally not retained, so its
            // uploaded source and converted audio have no remaining owner.
            // Do not leave either file behind merely because the model chose
            // the consent-mediated screen capture tool after transcription.
            if (wavPath is not null)
            {
                try { if (System.IO.File.Exists(wavPath)) System.IO.File.Delete(wavPath); } catch { }
            }
            if (validatedAudioFileName is not null &&
                TryResolveSafeAudioPath(_audioPath, validatedAudioFileName, out var transientAudioPath))
            {
                try { if (System.IO.File.Exists(transientAudioPath)) System.IO.File.Delete(transientAudioPath); } catch { }
            }

            var captureData = JsonSerializer.Serialize(new { screenCapture = new { prompt = messageText } });
            await Response.WriteAsync($"data: {captureData}\n\n");
            await Response.WriteAsync("data: [DONE]\n\n");
            await Response.Body.FlushAsync();
            await AwaitTurnSideEffectsAsync(instructionUpdateTask, Task.CompletedTask);
            return;
        }

        var memoryUpdateTask = !toolExecutions.Any(result =>
                                   result.Name.StartsWith("memory_", StringComparison.Ordinal) ||
                                   result.Name is "reminder_create" or "periodic_reminder_create")
            ? _longTermMemory.ExtractAndStoreAsync(
                userId, userMessage.Id, messageText, geminiKey, HttpContext.RequestAborted)
            : Task.CompletedTask;

        if (reminderDraft is { } reminder)
        {
            var reminderData = reminder.IsPeriodic
                ? JsonSerializer.Serialize(new
                {
                    periodicReminder = new
                    {
                        contractVersion = 2,
                        id = reminder.Id,
                        text = reminder.Text,
                        startAtUtc = reminder.DueAtUtc,
                        timeZoneId = reminder.TimeZoneId,
                        rrule = reminder.RecurrenceRule,
                        localNotificationId = reminder.LocalNotificationId
                    }
                })
                : JsonSerializer.Serialize(new
                {
                    reminder = new
                    {
                        id = reminder.Id,
                        text = reminder.Text,
                        dueAtUtc = reminder.DueAtUtc,
                        timeZoneId = reminder.TimeZoneId,
                        localNotificationId = reminder.LocalNotificationId
                    }
                });
            await Response.WriteAsync($"data: {reminderData}\n\n");
            await Response.Body.FlushAsync();

            var deliveryStatus = await _reminders.WaitForDeliveryStatusAsync(
                reminder.Id,
                reminder.DeviceId,
                TimeSpan.FromSeconds(30),
                HttpContext.RequestAborted);
            systemPrompt = deliveryStatus switch
            {
                ReminderDeliveryStatuses.Scheduled =>
                    reminder.IsPeriodic
                        ? $"Телефон зарегистрировал периодическое локальное напоминание «{reminder.Text}»: первый запуск {reminder.DueAtUtc:O}, правило {reminder.RecurrenceRule}. Кратко подтверди расписание и что оно работает локально без интернета; не обещай абсолютную минутную точность.\n\n{systemPrompt}"
                        : $"Телефон подтвердил локальное напоминание «{reminder.Text}» на {reminder.DueAtUtc:O}. Кратко подтверди пользователю, что оно установлено и сработает без интернета.\n\n{systemPrompt}",
                ReminderDeliveryStatuses.Failed =>
                    $"Телефон не смог установить локальное напоминание «{reminder.Text}». Честно сообщи об ошибке и не обещай, что напоминание сработает.\n\n{systemPrompt}",
                _ =>
                    $"Напоминание «{reminder.Text}» сохранено на сервере, но телефон ещё не подтвердил локальную установку. Не утверждай, что оно уже установлено; скажи, что требуется проверка уведомлений на телефоне.\n\n{systemPrompt}"
            };
        }

        // Cleanup wav file
        if (wavPath != null)
        {
            try { if (System.IO.File.Exists(wavPath)) System.IO.File.Delete(wavPath); } catch { }
        }

        var fullResponse = new System.Text.StringBuilder();
        var swLlm = Stopwatch.StartNew();
        long llmFirstTokenMs = 0;

        // A reminder is only confirmed after the phone responds above. Its
        // first tool-loop reply was generated before that receipt exists, so
        // use the verified receipt-aware prompt instead of risking an early
        // "set" confirmation from the model.
        var useAgentFinalText = !attemptedReminder && reminderDraft is null && !string.IsNullOrWhiteSpace(agentTurn.FinalText);

        // Kick off a fast, non-grounded "will this need search/deep thought?" check
        // in parallel with the real (possibly slow, search-grounded) response, so we
        // can speak a natural "hold on" phrase while the real answer is still cooking
        // instead of leaving the user in silence for several seconds. A reminder has
        // already waited for the device receipt, so a preamble would arrive too late.
        var preambleTask = !useAgentFinalText && actionProposal is null && reminderDraft is null
            ? GetPreambleIfNeededAsync(messageText, geminiKey, HttpContext.RequestAborted)
            : Task.FromResult<string?>(null);

        // Ordinary conversation retains the existing grounded streaming path.
        // A tool turn already has an evidence-aware final answer from the
        // native function-response loop, so emit it as one durable SSE chunk.
        var stream = useAgentFinalText
            ? StreamSingleResponseAsync(agentTurn.FinalText!)
            : _gemini.StreamResponseAsync(systemPrompt, messages, model: "gemini-3.5-flash", apiKey: geminiKey, cancellationToken: HttpContext.RequestAborted);
        var enumerator = stream.GetAsyncEnumerator(HttpContext.RequestAborted);

        try
        {
            var moveNextTask = enumerator.MoveNextAsync().AsTask();
            var winner = await Task.WhenAny(preambleTask, moveNextTask);

            if (winner == preambleTask && preambleTask.Result != null)
            {
                var preData = JsonSerializer.Serialize(new { preamble = preambleTask.Result });
                await Response.WriteAsync($"data: {preData}\n\n");
                await Response.Body.FlushAsync();
            }

            var hasMore = await moveNextTask;
            while (hasMore)
            {
                var chunk = enumerator.Current;
                if (fullResponse.Length == 0)
                {
                    llmFirstTokenMs = swLlm.ElapsedMilliseconds;
                }
                fullResponse.Append(chunk);
                var data = JsonSerializer.Serialize(new { text = chunk });
                await Response.WriteAsync($"data: {data}\n\n");
                await Response.Body.FlushAsync();
                hasMore = await enumerator.MoveNextAsync();
            }
        }
        catch (GeminiApiException ex)
        {
            // Infrastructure failure (missing key, non-2xx, connection error) --
            // GeminiService throws instead of yielding a fake content chunk
            // specifically so this never gets mistaken for a real reply and
            // saved to history (PROJECT-AUDIT-2026-07-10 REL-04). Tell the
            // client explicitly via a dedicated `error` SSE event (with a
            // retryability hint) instead of [DONE], and don't save
            // fullResponse -- it's empty anyway, since GeminiService only
            // ever throws before yielding its first real chunk.
            _logger.LogWarning(ex, "Gemini API error during chat response for session {SessionId}", req.SessionId);
            var errorData = JsonSerializer.Serialize(new { error = ex.Message, retryable = ex.IsRetryable });
            await Response.WriteAsync($"data: {errorData}\n\n");
            await Response.Body.FlushAsync();
            await AwaitTurnSideEffectsAsync(instructionUpdateTask, memoryUpdateTask);
            return;
        }
        catch (OperationCanceledException)
        {
            // Client abandoned this turn (e.g. user kept talking) — stop without
            // saving a partial/incomplete assistant reply. The user's message stays saved.
            _logger.LogInformation("Chat stream cancelled by client for session {SessionId}", req.SessionId);
            await AwaitTurnSideEffectsAsync(instructionUpdateTask, memoryUpdateTask);
            return;
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
        if (fullResponse.Length == 0 && externalAction is not null)
        {
            var fallback = GetExternalActionFallback(externalAction.Type);
            fullResponse.Append(fallback);
            var fallbackData = JsonSerializer.Serialize(new { text = fallback });
            await Response.WriteAsync($"data: {fallbackData}\n\n");
            await Response.Body.FlushAsync();
        }

        if (fullResponse.Length == 0)
        {
            _logger.LogWarning("Gemini returned no visible text for session {SessionId}; retrying without grounding", req.SessionId);
            try
            {
                await foreach (var chunk in _gemini.StreamResponseAsync(
                                   systemPrompt, messages, model: "gemini-3.5-flash", apiKey: geminiKey,
                                   enableGrounding: false, cancellationToken: HttpContext.RequestAborted))
                {
                    if (fullResponse.Length == 0) llmFirstTokenMs = swLlm.ElapsedMilliseconds;
                    fullResponse.Append(chunk);
                    var data = JsonSerializer.Serialize(new { text = chunk });
                    await Response.WriteAsync($"data: {data}\n\n");
                    await Response.Body.FlushAsync();
                }
            }
            catch (GeminiApiException ex)
            {
                _logger.LogWarning(ex, "Gemini retry failed for session {SessionId}", req.SessionId);
            }
        }

        swLlm.Stop();
        long llmTotalMs = swLlm.ElapsedMilliseconds;

        if (fullResponse.Length == 0)
        {
            _logger.LogWarning("Gemini returned no visible text for session {SessionId}", req.SessionId);
            var errorData = JsonSerializer.Serialize(new
            {
                error = "ИИ не вернул текстовый ответ. Попробуйте еще раз.",
                retryable = true
            });
            await Response.WriteAsync($"data: {errorData}\n\n");
            await Response.Body.FlushAsync();
            await AwaitTurnSideEffectsAsync(instructionUpdateTask, memoryUpdateTask);
            return;
        }

        // Save assistant response BEFORE any terminal SSE event -- the mobile
        // client stops reading the instant it sees [DONE] (PROJECT-AUDIT-2026-07-10
        // REL-02), so [DONE] must only ever mean "this turn is durably
        // persisted," not just "streaming finished." [DONE] used to be sent
        // first: a save failure after that point still looked successful to
        // the client, and stats (sent even later) were practically
        // unreachable for mobile since it had already stopped reading.
        session.Messages.Add(new Message { Role = "assistant", Content = fullResponse.ToString() });
        user.LastActiveAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Log stats to server logs
        _logger.LogInformation("VoiceAssistant Performance Stats - User: {UserEmail}, Session: {SessionId}, Convert: {ConvertMs}ms, Transcribe: {TranscribeMs}ms, SpeakerId: {SpeakerIdMs}ms, LLM (First Token): {LlmFirstMs}ms, LLM (Total): {LlmTotalMs}ms",
            user.Email, req.SessionId, convertMs, transcribeMs, speakerIdMs, llmFirstTokenMs, llmTotalMs);

        // Send stats event to client, then [DONE] last -- both only after the
        // save above has actually completed.
        var stats = new
        {
            ConvertMs = convertMs,
            TranscribeMs = transcribeMs,
            SpeakerIdMs = speakerIdMs,
            LlmFirstTokenMs = llmFirstTokenMs,
            LlmTotalMs = llmTotalMs,
            TranslationMs = 0L
        };
        var statsData = JsonSerializer.Serialize(new { stats }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await Response.WriteAsync($"data: {statsData}\n\n");
        await Response.Body.FlushAsync();

        await Response.WriteAsync("data: [DONE]\n\n");
        await Response.Body.FlushAsync();

        // Non-critical side calls (custom-instruction detection and fact
        // extraction) are awaited after [DONE], so they add no perceived
        // latency. Both use factory-created DbContexts and can safely run
        // alongside the main request context (PROJECT-AUDIT-2026-07-10 REL-01).
        await AwaitTurnSideEffectsAsync(instructionUpdateTask, memoryUpdateTask);

        // Unlike the call above, CheckAndUpdateAsync shares the request's
        // scoped _db (ConversationMemoryService is constructed with it
        // directly, not a factory) -- fine ONLY because it's awaited here,
        // strictly after the SaveChangesAsync above has fully completed, so
        // this is sequential reuse of one context, not concurrent access.
        // If this line is ever changed to run concurrently like the two tasks
        // above, give it its own DbContext first.
        await _conversationMemory.CheckAndUpdateAsync(session, geminiKey, HttpContext.RequestAborted);
    }

    private static async IAsyncEnumerable<string> StreamSingleResponseAsync(string response)
    {
        await Task.CompletedTask;
        yield return response;
    }

    // Rejects an oversized request body before model binding buffers the
    // whole thing into an IFormFile — the file.Length check below is a
    // second, independent layer, not a substitute (PROJECT-AUDIT-2026-07-10
    // SEC-07: "upload endpoints rely on model binding before checking file
    // length").
    [HttpPost("upload-audio")]
    [RequestSizeLimit(MaxAudioRequestBodySize)]
    public async Task<IActionResult> UploadAudio(IFormFile file)
    {
        if (file.Length == 0 || file.Length > MaxAudioSize)
        {
            _logger.LogWarning("upload-audio rejected: Length={Length}, ContentType={ContentType}, FileName={FileName}",
                file.Length, file.ContentType, file.FileName);
            return BadRequest(new { error = "File must be between 1 byte and 5MB" });
        }

        if (!file.ContentType.StartsWith("audio/"))
        {
            _logger.LogWarning("upload-audio rejected: unexpected ContentType={ContentType}, Length={Length}, FileName={FileName}",
                file.ContentType, file.Length, file.FileName);
            return BadRequest(new { error = "Only audio files are allowed" });
        }

        Directory.CreateDirectory(_audioPath);
        var fileName = $"{Guid.NewGuid()}.webm";
        var filePath = Path.Combine(_audioPath, fileName);

        await using var stream = new FileStream(filePath, FileMode.Create);
        await file.CopyToAsync(stream);

        return Ok(new { fileName });
    }

    // Checks a not-yet-finalized recording snapshot: transcribes it and judges whether
    // the speaker sounds done with their thought or is likely still talking/pausing to
    // think. Used to give real conversational patience instead of a fixed silence cutoff.
    // Nothing here gets persisted — the snapshot is a scratch file, deleted immediately after.
    [HttpPost("check-utterance")]
    [RequestSizeLimit(MaxAudioRequestBodySize)]
    public async Task<IActionResult> CheckUtterance(IFormFile audio)
    {
        if (audio.Length == 0 || audio.Length > MaxAudioSize)
            return BadRequest(new { error = "File must be between 1 byte and 5MB" });

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var settings = await _db.UserSettings.FirstOrDefaultAsync(s => s.UserId == userId);
        var geminiKey = settings?.GeminiApiKey;

        var tempWebm = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.webm");
        string? wavPath = null;
        try
        {
            await using (var stream = new FileStream(tempWebm, FileMode.Create))
            {
                await audio.CopyToAsync(stream);
            }

            wavPath = await ConvertToWavAsync(tempWebm);
            if (wavPath == null)
            {
                // Most often a clip with valid container framing but zero
                // actual audio samples (ffmpeg: "Could not find codec
                // parameters ... unknown codec") — a caller stopped the
                // recording essentially the instant it started. Treat the
                // same as "converted fine but nothing to transcribe" rather
                // than a hard error: both callers of this endpoint already
                // handle transcription="" gracefully (web: falls through to
                // the "not complete yet" branch; mobile: skips folding
                // anything into the pending turn text), so there's nothing
                // a 400 communicates that an empty result doesn't already.
                _logger.LogWarning("check-utterance: unconvertable audio, treating as no speech");
                return Ok(new { transcription = "", complete = false });
            }

            var result = await _audioAnalysis.CheckUtteranceCompletionAsync(wavPath, geminiKey);
            if (result == null) return StatusCode(502, new { error = "check failed" });

            return Ok(new { transcription = result.Transcription, complete = result.Complete });
        }
        finally
        {
            try { if (System.IO.File.Exists(tempWebm)) System.IO.File.Delete(tempWebm); } catch { }
            try { if (wavPath != null && System.IO.File.Exists(wavPath)) System.IO.File.Delete(wavPath); } catch { }
        }
    }

    [HttpGet("audio/{fileName}")]
    public async Task<IActionResult> GetAudio(string fileName)
    {
        if (fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
            return BadRequest();

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

        var ownsAudio = await _db.ChatSessions
            .Where(s => s.UserId == userId)
            .SelectMany(s => s.Messages)
            .AnyAsync(m => m.AudioFileName == fileName);

        if (!ownsAudio) return NotFound();

        var filePath = Path.Combine(_audioPath, fileName);
        if (!System.IO.File.Exists(filePath)) return NotFound();

        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        return File(stream, "audio/webm");
    }

    [HttpPost("ocr-image")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> OcrImage(IFormFile file)
    {
        if (file.Length == 0 || file.Length > MaxImageSize)
            return BadRequest(new { error = "File must be between 1 byte and 10MB" });

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var bytes = ms.ToArray();

        if (!TryDetectImageMimeType(bytes, out var detectedMimeType))
            return BadRequest(new { error = "Only JPEG, PNG, GIF, or WebP images are allowed" });

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var settings = await _db.UserSettings.FirstOrDefaultAsync(s => s.UserId == userId);
        var geminiKey = settings?.GeminiApiKey;
        var key = string.IsNullOrWhiteSpace(geminiKey)
            ? _config["Gemini:ApiKey"]
            : geminiKey;
        if (string.IsNullOrWhiteSpace(key))
            return BadRequest(new { error = "Gemini API key is not configured" });

        var base64 = Convert.ToBase64String(bytes);

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(30);

        var payload = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { inline_data = new { mime_type = detectedMimeType, data = base64 } },
                        new { text = "Extract ALL text from this image exactly as written. Preserve line breaks and formatting. Return ONLY the extracted text, nothing else." }
                    }
                }
            },
            generationConfig = new
            {
                maxOutputTokens = 4096,
                thinkingConfig = new { thinkingBudget = 0 }
            }
        };

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={key}";

        try
        {
            var response = await http.PostAsync(url,
                new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json"));

            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Gemini OCR error {Status}: {Body}", response.StatusCode, body);
                return StatusCode(502, new { error = "OCR service error" });
            }

            var doc = JsonDocument.Parse(body);
            var parts = doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts");
            var text = string.Join("", parts.EnumerateArray()
                .Where(p => p.TryGetProperty("text", out _))
                .Select(p => p.GetProperty("text").GetString() ?? ""));

            return Ok(new { text = text.Trim() });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gemini OCR failed");
            return StatusCode(502, new { error = "OCR service error" });
        }
    }

    // Fast, non-grounded check: will answering this well require web search or
    // careful multi-step reasoning? If so, returns a short natural "hold on" phrase
    // to speak while the real (possibly slow) response is still generating.
    // Returns null for ordinary conversational messages that don't need a heads-up.
    private async Task<string?> GetPreambleIfNeededAsync(string userMessage, string? geminiKey, CancellationToken ct)
    {
        var prompt = $$"""
            Сообщение пользователя: "{{userMessage}}"
            Чтобы дать на него хороший ответ, ассистенту понадобится (а) искать актуальную информацию в интернете или (б) тщательно обдумывать сложную, многогранную задачу?
            Если да — выведи короткую, естественную, каждый раз разную по формулировке русскую фразу-предупреждение о паузе (например "Секунду, поищу это..." или "Дай мне подумать над этим..."), без кавычек.
            Если это обычный разговорный вопрос, не требующий поиска или долгих раздумий — выведи ровно NONE.
            Ничего кроме фразы или NONE не пиши.
            """;

        var messages = new List<GeminiMessage> { new("user", prompt) };
        var sb = new System.Text.StringBuilder();
        try
        {
            await foreach (var chunk in _gemini.StreamResponseAsync("", messages, model: "gemini-3.5-flash",
                maxTokens: PreambleCheckMaxTokens, apiKey: geminiKey, enableGrounding: false, cancellationToken: ct))
            {
                sb.Append(chunk);
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Preamble check failed");
            return null;
        }

        var result = sb.ToString().Trim().Trim('"');
        if (string.IsNullOrEmpty(result) || result.Equals("NONE", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return result;
    }

    private async Task AwaitTurnSideEffectsAsync(params Task[] tasks)
    {
        foreach (var task in tasks)
        {
            try
            {
                await task;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Non-critical turn side effect failed");
            }
        }
    }

    // Detects whether the user asked the assistant to remember a persistent behavior
    // preference ("говори медленнее", "давай на ты", "не используй сложные слова")
    // and, if so, merges it into UserSettings.CustomSystemPrompt so every future
    // system prompt reflects it. No-ops for ordinary conversational messages.
    private async Task MaybeUpdateCustomInstructionsAsync(string userId, string userMessage, string? existingInstructions, string? geminiKey, CancellationToken ct)
    {
        var prompt = $$"""
            Текущие постоянные инструкции о поведении ассистента (могут быть пустыми):
            ---
            {{existingInstructions ?? "(пока нет)"}}
            ---
            Новое сообщение пользователя: "{{userMessage}}"

            Если пользователь просит ЗАПОМНИТЬ что-то о том, как ассистент должен вести себя в будущих разговорах (например: "запомни, говори медленнее", "давай на ты", "не используй сложные слова", "больше не упоминай X") — выведи ПОЛНЫЙ обновлённый список инструкций (объедини старые с новой просьбой, убери противоречия и дубликаты), кратко, по-русски.
            Если пользователь НЕ просит ничего подобного запомнить — выведи ровно NONE.
            Ничего кроме итоговых инструкций или NONE не пиши.
            """;

        var messages = new List<GeminiMessage> { new("user", prompt) };
        var sb = new System.Text.StringBuilder();
        try
        {
            await foreach (var chunk in _gemini.StreamResponseAsync("", messages, model: "gemini-3.5-flash",
                maxTokens: CustomInstructionCheckMaxTokens, apiKey: geminiKey, enableGrounding: false, cancellationToken: ct))
            {
                sb.Append(chunk);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Custom-instruction check failed");
            return;
        }

        var result = sb.ToString().Trim().Trim('"');
        if (string.IsNullOrEmpty(result) || result.Equals("NONE", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // This method runs concurrently with the rest of Send() (see the
        // instructionUpdateTask comment at the call site) -- it must not
        // share the request's scoped _db with the main flow's own
        // SaveChangesAsync, or the two can race on the same DbContext
        // instance (PROJECT-AUDIT-2026-07-10 REL-01).
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var settings = await db.UserSettings.FirstOrDefaultAsync(s => s.UserId == userId);
        if (settings == null)
        {
            settings = new Data.Entities.UserSettings { UserId = userId, CustomSystemPrompt = result };
            db.UserSettings.Add(settings);
        }
        else
        {
            settings.CustomSystemPrompt = result;
            settings.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
        _logger.LogInformation("Updated custom instructions for user {UserId}: {Instructions}", userId, result);
    }

    private async Task<string?> ConvertToWavAsync(string webmPath)
    {
        var wavPath = Path.ChangeExtension(webmPath, ".wav");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-i \"{webmPath}\" -ar 16000 -ac 1 -y \"{wavPath}\"",
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            // Must read stderr to prevent deadlock (ffmpeg writes verbose output)
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            
            if (process.ExitCode != 0)
            {
                _logger.LogError("ffmpeg conversion failed for {File}. ExitCode: {Code}. Stderr: {Stderr}", webmPath, process.ExitCode, stderr);
                return null;
            }
            
            return System.IO.File.Exists(wavPath) ? wavPath : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during ffmpeg conversion for {File}", webmPath);
            return null;
        }
    }
}
