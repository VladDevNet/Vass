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
    private static readonly Regex InternalProtocolReplyPattern = new(
        @"^\s*(?:(?:reminder_create|periodic_reminder_create|reminder_list|reminder_cancel|memory_status|memory_list|memory_search|memory_remember|memory_correct|memory_forget|conversation_search|web_search|library_list|library_write|library_open|capability_help|capability_discovery_status|capability_discovery_candidates|capability_discovery_present|capability_discovery_decline|screen_capture_once|open_vass|youtube_search|youtube_watch)\s*[\(\{]|(?:search|function(?:Call|Response)?|tool(?:Call|Response)?)\s*\{)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly AppDbContext _db;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly UserManager<User> _userManager;
    private readonly GeminiService _gemini;
    private readonly IPrimaryConversationService _primaryConversation;
    private readonly CompanionPromptService _tutor;
    private readonly AudioAnalysisService _audioAnalysis;
    private readonly AudioCoreTranscriptionService _audioCoreTranscription;
    private readonly GroundedWebSearchService _groundedWebSearch;
    private readonly SpeakerRegistryService _speakerRegistry;
    private readonly ConversationMemoryService _conversationMemory;
    private readonly LongTermMemoryService _longTermMemory;
    private readonly ReminderService _reminders;
    private readonly VisualAssetService _visualAssets;
    private readonly AssistantCapabilityRegistry _capabilities;
    private readonly CapabilityDiscoveryService _capabilityDiscovery;
    private readonly ActionReceiptService _actionReceipts;
    private readonly AssistantAgentTurnService _agentTurn;
    private readonly IConfiguration _config;
    private readonly ILogger<ChatController> _logger;
    private readonly string _audioPath;

    public ChatController(AppDbContext db, IDbContextFactory<AppDbContext> dbContextFactory, UserManager<User> userManager,
        GeminiService gemini, IPrimaryConversationService primaryConversation, CompanionPromptService tutor,
        AudioAnalysisService audioAnalysis, AudioCoreTranscriptionService audioCoreTranscription,
        GroundedWebSearchService groundedWebSearch,
        SpeakerRegistryService speakerRegistry, ConversationMemoryService conversationMemory,
        LongTermMemoryService longTermMemory,
        ReminderService reminders,
        VisualAssetService visualAssets,
        AssistantCapabilityRegistry capabilities,
        CapabilityDiscoveryService capabilityDiscovery,
        ActionReceiptService actionReceipts,
        AssistantAgentTurnService agentTurn,
        IConfiguration config, IWebHostEnvironment env, ILogger<ChatController> logger)
    {
        _db = db;
        _dbContextFactory = dbContextFactory;
        _userManager = userManager;
        _gemini = gemini;
        _primaryConversation = primaryConversation;
        _tutor = tutor;
        _audioAnalysis = audioAnalysis;
        _audioCoreTranscription = audioCoreTranscription;
        _groundedWebSearch = groundedWebSearch;
        _speakerRegistry = speakerRegistry;
        _conversationMemory = conversationMemory;
        _longTermMemory = longTermMemory;
        _reminders = reminders;
        _visualAssets = visualAssets;
        _capabilities = capabilities;
        _capabilityDiscovery = capabilityDiscovery;
        _actionReceipts = actionReceipts;
        _agentTurn = agentTurn;
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

    // AudioCore is the production voice path for every account. If the
    // primary-model call itself is unavailable, the existing ASR path below
    // remains the per-turn compatibility fallback; this is not a rollout
    // switch and has no user- or environment-level gate.

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
        ExternalActionTypes.AssistantSleep => "Ставлю слушание на паузу.",
        ExternalActionTypes.YouTubeWatch => "Открываю выбранное видео в YouTube.",
        ExternalActionTypes.LibraryWrite => "Сохраняю книгу в вашу библиотеку.",
        ExternalActionTypes.LibraryOpen => "Открываю вашу библиотеку.",
        _ => "Открываю поиск в YouTube."
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

    // The catalog is owned by the device and may contain model-authored text,
    // so it is bounded and normalized before being placed in any prompt. The
    // server stores neither this list nor its documents.
    private static IReadOnlyList<AssistantLibraryCatalogItem> NormalizeLibraryCatalog(
        IReadOnlyList<LibraryCatalogRequestItem>? catalog)
    {
        if (catalog is null || catalog.Count == 0) return [];
        var items = new List<AssistantLibraryCatalogItem>();
        var seen = new HashSet<Guid>();
        foreach (var item in catalog.Take(20))
        {
            if (!Guid.TryParse(item.Id, out var id) || !seen.Add(id)) continue;
            var title = NormalizeLibraryCatalogText(item.Title, 120);
            if (string.IsNullOrWhiteSpace(title)) continue;
            var kindCandidate = item.Kind?.Trim().ToLowerInvariant();
            var kind = kindCandidate switch
            {
                "recipes" or "restaurants" or "entertainment" or "guide" or "other" => kindCandidate,
                _ => "other"
            };
            items.Add(new AssistantLibraryCatalogItem(
                id.ToString(),
                title!,
                kind,
                NormalizeLibraryCatalogText(item.Summary, 600),
                Math.Clamp(item.RevisionCount, 1, 50),
                NormalizeLibraryCatalogText(item.SectionTitle, 60)));
        }
        return items;
    }

    private static IReadOnlyList<AssistantLibrarySectionItem> NormalizeLibrarySections(
        IReadOnlyList<LibrarySectionRequestItem>? sections)
    {
        if (sections is null || sections.Count == 0) return [];
        var items = new List<AssistantLibrarySectionItem>();
        var seenIds = new HashSet<Guid>();
        var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in sections.Take(30))
        {
            if (!Guid.TryParse(section.Id, out var id) || !seenIds.Add(id)) continue;
            var title = NormalizeLibraryCatalogText(section.Title, 60);
            if (string.IsNullOrWhiteSpace(title) || !seenTitles.Add(title)) continue;
            items.Add(new AssistantLibrarySectionItem(id.ToString(), title));
        }
        return items;
    }

    private static string? NormalizeLibraryCatalogText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    public sealed record LibraryCatalogRequestItem(
        string Id,
        string Title,
        string Kind,
        string? Summary = null,
        int RevisionCount = 1,
        string? SectionTitle = null);

    public sealed record LibrarySectionRequestItem(
        string Id,
        string Title);

    public record SendRequest(
        int SessionId,
        string Message,
        string? AudioFileName = null,
        string? DeviceId = null,
        string? TimeZoneId = null,
        bool SupportsExternalActions = false,
        Guid? VisualAssetId = null,
        bool SupportsScreenAnalysis = false,
        bool SupportsLibrary = false,
        bool SupportsSpeechText = false,
        IReadOnlyList<LibraryCatalogRequestItem>? LibraryCatalog = null,
        IReadOnlyList<LibrarySectionRequestItem>? LibrarySections = null,
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
        var turnTimeline = Stopwatch.StartNew();
        long? audioCoreReadyAtMs = null;
        long? preambleSentAtMs = null;
        long? transcriptionSentAtMs = null;
        long? memoryRecallReadyAtMs = null;
        long? agentReadyAtMs = null;
        long? llmStartedAtMs = null;
        long? firstSpeechTextSentAtMs = null;
        long? firstTextSentAtMs = null;
        long? responsePersistedAtMs = null;
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
        long audioCoreMs = 0;
        var audioCoreUsed = false;
        var audioCoreFallback = false;
        AudioCoreTranscriptionResult? audioCoreResult = null;
        long memoryRecallMs = 0;
        long agentMs = 0;
        var agentSkipped = false;
        long speakerIdMs = 0;
        SpeakerIdResult? speakerResult = null;
        // Tracks "did we enter an audio transcription route at all" — this
        // must stay independent from the resulting text. Both the direct
        // AudioCore function call and the legacy ASR can deliberately return
        // an empty transcript for silence, while the legacy route also uses
        // null for provider failures and its anti-hallucination guards.
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

            var sw = Stopwatch.StartNew();
            audioCoreResult = await _audioCoreTranscription.TranscribeAsync(filePath, geminiKey, HttpContext.RequestAborted);
            sw.Stop();
            audioCoreMs = sw.ElapsedMilliseconds;
            audioCoreReadyAtMs = turnTimeline.ElapsedMilliseconds;
            _logger.LogInformation(
                "Voice turn timeline audio core ready: Turn {ClientTurnId}, At {ElapsedMs}ms, Duration {AudioCoreMs}ms, ProviderAvailable {ProviderAvailable}",
                req.ClientTurnId, audioCoreReadyAtMs, audioCoreMs, audioCoreResult.ProviderAvailable);

            if (audioCoreResult.ProviderAvailable)
            {
                transcription = audioCoreResult.Transcription;
                audioCoreUsed = true;
            }
            else
            {
                // Keep the proven ffmpeg + Gemini 2.5 ASR route as a
                // per-turn compatibility fallback when the direct model
                // rejects a particular upload or has a provider incident.
                audioCoreFallback = true;
            }

            if (!audioCoreUsed)
            {
                // The legacy processor is also the compatibility path for
                // older uploads whose container the direct model rejects.
                sw.Restart();
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
            }

            messageText = transcription;

            // Disabled by default via Features:SpeakerIdentificationEnabled (see
            // SpeakerRegistryService — PROJECT-AUDIT-2026-07-10 SEC-02, no tenant
            // isolation yet) — IdentifyAsync no-ops immediately when that's unset,
            // so this call is always cheap while off. Independently ALSO not worth
            // enabling yet even once isolation is added: real short phone-mic clips
            // scored too close to noise-floor similarity in testing (~0.18-0.28,
            // near the ~0.16 seen between different synthetic voices) to trust, at
            // 400ms-2s cost per turn for an unproven payoff.
            if (wavPath is not null && !string.IsNullOrWhiteSpace(transcription))
            {
                sw.Restart();
                speakerResult = await _speakerRegistry.IdentifyAsync(wavPath, transcription, geminiKey);
                sw.Stop();
                speakerIdMs = sw.ElapsedMilliseconds;
            }
        }

        // The central model receives the original request and share payload
        // together, so it can form a meaningful memory entry that retains a
        // complete URL instead of a client-side regex extracting fragments.
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

        // AudioCore has already classified this as a request for fresh public
        // information. Start the independent lookup before memory recall and
        // the first agent plan, then hand its single result to web_search if
        // the planner confirms that action.
        GroundedWebSearchPrefetch? webSearchPrefetch = null;
        if (audioCoreUsed &&
            audioCoreResult is { RequiresWebSearch: true } &&
            visualAsset is null &&
            sharedContent is null)
        {
            webSearchPrefetch = new GroundedWebSearchPrefetch(
                _groundedWebSearch.SearchAsync(
                    messageText,
                    geminiKey,
                    HttpContext.RequestAborted,
                    "voice_prefetch"));
            _logger.LogInformation(
                "Voice turn web search prefetch started: Turn {ClientTurnId}; At {ElapsedMs}ms",
                req.ClientTurnId,
                turnTimeline.ElapsedMilliseconds);
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
            ClientTurnId: req.ClientTurnId,
            VisualAssetId: visualAsset?.Id,
            SupportsLibrary: req.SupportsLibrary,
            LibraryCatalog: NormalizeLibraryCatalog(req.LibraryCatalog),
            LibrarySections: NormalizeLibrarySections(req.LibrarySections));
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
        if (visualAsset is not null)
            await _capabilityDiscovery.MarkUsedAsync(userId, "visual", HttpContext.RequestAborted);
        if (sharedContent is not null)
            await _capabilityDiscovery.MarkUsedAsync(userId, "share", HttpContext.RequestAborted);

        // For an AudioCore voice turn the primary model has already supplied
        // a short, safe phrase in the native function call. Emit it before
        // memory recall and tool planning so the user hears a real response
        // while the detailed agent path is still working.
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";
        var preambleSent = false;
        var transcriptionSent = false;
        if (audioCoreUsed && !string.IsNullOrWhiteSpace(audioCoreResult?.Preamble))
        {
            var earlyPreambleData = JsonSerializer.Serialize(new { preamble = audioCoreResult.Preamble });
            await Response.WriteAsync($"data: {earlyPreambleData}\n\n");
            await Response.Body.FlushAsync();
            preambleSent = true;
            preambleSentAtMs = turnTimeline.ElapsedMilliseconds;
            _logger.LogInformation("Voice turn timeline preamble sent: Turn {ClientTurnId}, At {ElapsedMs}ms", req.ClientTurnId, preambleSentAtMs);
        }
        if (transcription is not null)
        {
            var earlyTranscriptionData = JsonSerializer.Serialize(new { transcription });
            await Response.WriteAsync($"data: {earlyTranscriptionData}\n\n");
            await Response.Body.FlushAsync();
            transcriptionSent = true;
            transcriptionSentAtMs = turnTimeline.ElapsedMilliseconds;
            _logger.LogInformation("Voice turn timeline transcription sent: Turn {ClientTurnId}, At {ElapsedMs}ms", req.ClientTurnId, transcriptionSentAtMs);
        }

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
        var recallStopwatch = Stopwatch.StartNew();
        var recalledFacts = await _longTermMemory.RecallAsync(userId, messageText, geminiKey, HttpContext.RequestAborted);
        recallStopwatch.Stop();
        memoryRecallMs = recallStopwatch.ElapsedMilliseconds;
        memoryRecallReadyAtMs = turnTimeline.ElapsedMilliseconds;

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
        var discoveryTurn = await _capabilityDiscovery.GetTurnContextAsync(
            userId,
            capabilityContext,
            HttpContext.RequestAborted);
        if (!string.IsNullOrWhiteSpace(discoveryTurn.Prompt))
            systemPrompt = discoveryTurn.Prompt + "\n\n" + systemPrompt;

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

        // The AudioCore model is deliberately conservative: it permits this
        // bypass only for ordinary conversation and labels uncertainty as
        // requiring a tool. Attachments always retain the full planner so
        // screenshots, shares, reminders and memory actions cannot be
        // silently downgraded into plain text replies.
        var bypassAgentPlanner = audioCoreUsed &&
                                 audioCoreResult is { RequiresTool: false } &&
                                 visualAsset is null &&
                                 sharedContent is null &&
                                 !discoveryTurn.RequiresAgentPlanner;
        var agentStopwatch = Stopwatch.StartNew();
        var webSearchProgressCount = 0;
        AssistantAgentTurnResult agentTurn;
        if (bypassAgentPlanner)
        {
            agentSkipped = true;
            agentTurn = new AssistantAgentTurnResult(false, [], null, false, false, false);
        }
        else
        {
            var agentCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var progressTask = webSearchPrefetch is null
                ? Task.CompletedTask
                : EmitVoiceWebSearchProgressAsync(
                    agentCompleted.Task,
                    req.ClientTurnId,
                    turnTimeline,
                    () => webSearchProgressCount++,
                    HttpContext.RequestAborted);
            try
            {
                agentTurn = await _agentTurn.RunAsync(
                    systemPrompt,
                    messages,
                    geminiKey,
                    userId,
                    userMessage.Id,
                    capabilityContext,
                    webSearchPrefetch,
                    HttpContext.RequestAborted);
            }
            finally
            {
                agentCompleted.TrySetResult();
            }
            await progressTask;
        }
        agentStopwatch.Stop();
        agentMs = agentStopwatch.ElapsedMilliseconds;
        agentReadyAtMs = turnTimeline.ElapsedMilliseconds;
        var toolExecutions = agentTurn.ToolExecutions;
        var failedWebSearch = toolExecutions.LastOrDefault(execution =>
            execution.Name == "web_search" && execution.Status != "grounded");
        string? retriedWebSearchText = null;
        if (failedWebSearch is not null && TryGetWebSearchQuery(failedWebSearch, out var failedWebSearchQuery))
        {
            // The first lookup can fail transiently or return an incomplete
            // grounding payload. Tell the person what is happening, then
            // make one bounded retry before giving up on fresh facts.
            var retryNoticeData = JsonSerializer.Serialize(new { progressText = "Поиск дал сбой, повторяю запрос." });
            await Response.WriteAsync($"data: {retryNoticeData}\n\n", HttpContext.RequestAborted);
            await Response.Body.FlushAsync(HttpContext.RequestAborted);
            webSearchProgressCount++;

            var retryResult = await _groundedWebSearch.SearchAsync(
                failedWebSearchQuery,
                geminiKey,
                HttpContext.RequestAborted,
                "server_retry_after_failed_search");
            if (retryResult.Status == "grounded")
            {
                retriedWebSearchText = retryResult.Summary;
                _logger.LogInformation(
                    "Grounded web search recovered on retry: Session {SessionId}; Sources {SourceCount}",
                    req.SessionId,
                    retryResult.Sources.Count);
            }
            else
            {
                _logger.LogWarning(
                    "Grounded web search retry did not recover: Session {SessionId}; InitialStatus {InitialStatus}; RetryStatus {RetryStatus}",
                    req.SessionId,
                    failedWebSearch.Status,
                    retryResult.Status);
            }
        }
        var screenCaptureRequested = agentTurn.RequestsScreenCapture;
        var reminderDraft = toolExecutions.Select(result => result.Reminder).FirstOrDefault(reminder => reminder is not null);
        var reminderDeliveryStatus = ReminderDeliveryStatuses.Pending;
        var reminderCancellation = toolExecutions
            .Select(result => result.ReminderCancellation)
            .FirstOrDefault(cancellation => cancellation is not null);
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
                externalAction.VideoId,
                externalAction.LibraryArtifact,
                externalAction.ArtifactId);

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

        // Response headers and the direct voice preamble are deliberately
        // sent above, before recall/planning. Keep this late transcription
        // branch for non-AudioCore requests only.
        if (transcription != null && !transcriptionSent)
        {
            var trData = JsonSerializer.Serialize(new { transcription });
            await Response.WriteAsync($"data: {trData}\n\n");
            await Response.Body.FlushAsync();
            transcriptionSent = true;
            transcriptionSentAtMs = turnTimeline.ElapsedMilliseconds;
            _logger.LogInformation("Voice turn timeline transcription sent: Turn {ClientTurnId}, At {ElapsedMs}ms", req.ClientTurnId, transcriptionSentAtMs);
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
                    videoId = actionProposal.VideoId,
                    libraryArtifact = actionProposal.LibraryArtifact,
                    artifactId = actionProposal.ArtifactId
                }
            });
            await Response.WriteAsync($"data: {actionData}\n\n");
            await Response.Body.FlushAsync();
        }

        if (reminderCancellation is not null)
        {
            var cancellationData = JsonSerializer.Serialize(new
            {
                reminderCancelled = new
                {
                    id = reminderCancellation.ReminderId,
                    text = reminderCancellation.Text,
                    deliveries = reminderCancellation.Deliveries.Select(delivery => new
                    {
                        deviceId = delivery.DeviceId,
                        localNotificationId = delivery.LocalNotificationId
                    })
                }
            });
            await Response.WriteAsync($"data: {cancellationData}\n\n");
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

        var memoryUpdateTask = !agentTurn.UsedTools
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

            reminderDeliveryStatus = await _reminders.WaitForDeliveryStatusAsync(
                reminder.Id,
                reminder.DeviceId,
                TimeSpan.FromSeconds(30),
                HttpContext.RequestAborted);
        }

        // Cleanup wav file
        if (wavPath != null)
        {
            try { if (System.IO.File.Exists(wavPath)) System.IO.File.Delete(wavPath); } catch { }
        }

        var fullResponse = new System.Text.StringBuilder();
        var swLlm = Stopwatch.StartNew();
        long llmFirstTokenMs = 0;
        var llmFirstChunkReceived = false;
        llmStartedAtMs = turnTimeline.ElapsedMilliseconds;

        // A reminder is only confirmed after the phone responds above. Keep
        // that final receipt deterministic: a second free-form model call can
        // mistakenly print a tool invocation instead of a human reply.
        var agentFinalText = agentTurn.FinalText?.Trim();
        var rejectedInternalProtocolText = LooksLikeInternalProtocolReply(agentFinalText);
        if (rejectedInternalProtocolText)
        {
            _logger.LogWarning("Rejected internal protocol text from agent final reply for session {SessionId}", req.SessionId);
            agentFinalText = null;
        }

        var webSearchFailed = failedWebSearch is not null && retriedWebSearchText is null;

        var responseOverride = reminderDraft is { } receiptReminder
            ? BuildReminderReceiptReply(receiptReminder, reminderDeliveryStatus)
            : externalAction is not null
                ? GetExternalActionFallback(externalAction.Type)
            : retriedWebSearchText is not null
                ? retriedWebSearchText
            : webSearchFailed
                ? "Сейчас не удалось подтвердить свежие сведения по надежным источникам. Попробуйте повторить запрос немного позже."
            : !attemptedReminder && agentFinalText is not null
                ? agentFinalText
                : rejectedInternalProtocolText
                    ? GetSafeToolFallback(toolExecutions, externalAction)
                    : null;
        var useAgentFinalText = responseOverride is not null;
        // Do not let the model rewrite normal Russian for TTS. The mobile
        // client already speaks the visible response when no speechText event
        // is sent, which keeps pronunciation under the device TTS engine.
        var responseSystemPrompt = systemPrompt;

        // Kick off a fast, non-grounded "will this need search/deep thought?" check
        // in parallel with the real (possibly slow, search-grounded) response, so we
        // can speak a natural "hold on" phrase while the real answer is still cooking
        // instead of leaving the user in silence for several seconds. A reminder has
        // already waited for the device receipt, so a preamble would arrive too late.
        var preambleTask = !preambleSent && !useAgentFinalText && actionProposal is null && reminderDraft is null
            ? GetPreambleIfNeededAsync(messageText, geminiKey, HttpContext.RequestAborted)
            : Task.FromResult<string?>(null);

        // Ordinary conversation retains the existing grounded streaming path.
        // A tool turn already has an evidence-aware final answer from the
        // native function-response loop, so emit it as one durable SSE chunk.
        var stream = useAgentFinalText
            ? StreamSingleResponseAsync(responseOverride!)
            : _primaryConversation.StreamResponseAsync(responseSystemPrompt, messages, maxTokens: 8192, geminiApiKey: geminiKey,
                enableGrounding: true, cancellationToken: HttpContext.RequestAborted);
        var enumerator = stream.GetAsyncEnumerator(HttpContext.RequestAborted);
        var speechFirstParser = new SpeechFirstResponseParser();

        async Task EmitResponsePartsAsync(IEnumerable<SpeechFirstResponseChunk> parts)
        {
            foreach (var part in parts)
            {
                if (part.Part == SpeechFirstResponsePart.Speech)
                {
                    var speechData = JsonSerializer.Serialize(new { speechText = part.Text });
                    await Response.WriteAsync($"data: {speechData}\n\n");
                    await Response.Body.FlushAsync();
                    firstSpeechTextSentAtMs ??= turnTimeline.ElapsedMilliseconds;
                    continue;
                }

                fullResponse.Append(part.Text);
                var textData = JsonSerializer.Serialize(new { text = part.Text });
                await Response.WriteAsync($"data: {textData}\n\n");
                await Response.Body.FlushAsync();
                firstTextSentAtMs ??= turnTimeline.ElapsedMilliseconds;
            }
        }

        try
        {
            var moveNextTask = enumerator.MoveNextAsync().AsTask();
            var winner = await Task.WhenAny(preambleTask, moveNextTask);

            if (winner == preambleTask && preambleTask.Result != null)
            {
                var preData = JsonSerializer.Serialize(new { preamble = preambleTask.Result });
                await Response.WriteAsync($"data: {preData}\n\n");
                await Response.Body.FlushAsync();
                preambleSent = true;
                preambleSentAtMs = turnTimeline.ElapsedMilliseconds;
                _logger.LogInformation("Voice turn timeline preamble sent: Turn {ClientTurnId}, At {ElapsedMs}ms", req.ClientTurnId, preambleSentAtMs);
            }

            var hasMore = await moveNextTask;
            while (hasMore)
            {
                var chunk = enumerator.Current;
                if (!llmFirstChunkReceived)
                {
                    llmFirstTokenMs = swLlm.ElapsedMilliseconds;
                    llmFirstChunkReceived = true;
                }
                await EmitResponsePartsAsync(speechFirstParser.Append(chunk));
                hasMore = await enumerator.MoveNextAsync();
            }
        }
        catch (ModelApiException ex)
        {
            // Infrastructure failure (missing key, non-2xx, connection error) --
            // GeminiService throws instead of yielding a fake content chunk
            // specifically so this never gets mistaken for a real reply and
            // saved to history (PROJECT-AUDIT-2026-07-10 REL-04). Tell the
            // client explicitly via a dedicated `error` SSE event (with a
            // retryability hint) instead of [DONE], and don't save
            // fullResponse -- it's empty anyway, since GeminiService only
            // ever throws before yielding its first real chunk.
            _logger.LogWarning(ex, "Primary model API error during chat response for session {SessionId}; Provider {Provider}", req.SessionId, _primaryConversation.Provider);
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
        await EmitResponsePartsAsync(speechFirstParser.Finish());
        if (fullResponse.Length == 0 && externalAction is not null)
        {
            var fallback = GetExternalActionFallback(externalAction.Type);
            await EmitResponsePartsAsync([new SpeechFirstResponseChunk(SpeechFirstResponsePart.Text, fallback)]);
        }

        if (fullResponse.Length == 0)
        {
            _logger.LogWarning("Gemini returned no visible text for session {SessionId}; retrying without grounding", req.SessionId);
            try
            {
                var retryParser = new SpeechFirstResponseParser();
                await foreach (var chunk in _primaryConversation.StreamResponseAsync(
                                   responseSystemPrompt, messages, maxTokens: 8192, geminiApiKey: geminiKey,
                                   enableGrounding: false, cancellationToken: HttpContext.RequestAborted))
                {
                    if (!llmFirstChunkReceived)
                    {
                        llmFirstTokenMs = swLlm.ElapsedMilliseconds;
                        llmFirstChunkReceived = true;
                    }
                    await EmitResponsePartsAsync(retryParser.Append(chunk));
                }
                await EmitResponsePartsAsync(retryParser.Finish());
            }
            catch (ModelApiException ex)
            {
                _logger.LogWarning(ex, "Primary model retry failed for session {SessionId}; Provider {Provider}", req.SessionId, _primaryConversation.Provider);
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
        responsePersistedAtMs = turnTimeline.ElapsedMilliseconds;

        // Log stats to server logs
        _logger.LogInformation("VoiceAssistant Performance Stats - Turn: {ClientTurnId}, User: {UserEmail}, Session: {SessionId}, AudioCore: {AudioCoreMs}ms (Used: {AudioCoreUsed}, Fallback: {AudioCoreFallback}), MemoryRecall: {MemoryRecallMs}ms, Agent: {AgentMs}ms (Skipped: {AgentSkipped}), Preamble: {PreambleSent}, WebSearchProgress: {WebSearchProgressCount}, Convert: {ConvertMs}ms, Transcribe: {TranscribeMs}ms, SpeakerId: {SpeakerIdMs}ms, LLM (First Token): {LlmFirstMs}ms, LLM (Total): {LlmTotalMs}ms, Total: {TotalMs}ms",
            req.ClientTurnId, user.Email, req.SessionId, audioCoreMs, audioCoreUsed, audioCoreFallback, memoryRecallMs, agentMs, agentSkipped, preambleSent, webSearchProgressCount, convertMs, transcribeMs, speakerIdMs, llmFirstTokenMs, llmTotalMs, turnTimeline.ElapsedMilliseconds);

        // Send stats event to client, then [DONE] last -- both only after the
        // save above has actually completed.
        var stats = new
        {
            AudioCoreMs = audioCoreMs,
            AudioCoreUsed = audioCoreUsed,
            AudioCoreFallback = audioCoreFallback,
            MemoryRecallMs = memoryRecallMs,
            AgentMs = agentMs,
            AgentSkipped = agentSkipped,
            PreambleSent = preambleSent,
            WebSearchProgressCount = webSearchProgressCount,
            ConvertMs = convertMs,
            TranscribeMs = transcribeMs,
            SpeakerIdMs = speakerIdMs,
            LlmFirstTokenMs = llmFirstTokenMs,
            LlmTotalMs = llmTotalMs,
            AudioCoreReadyAtMs = audioCoreReadyAtMs,
            PreambleSentAtMs = preambleSentAtMs,
            TranscriptionSentAtMs = transcriptionSentAtMs,
            MemoryRecallReadyAtMs = memoryRecallReadyAtMs,
            AgentReadyAtMs = agentReadyAtMs,
            LlmStartedAtMs = llmStartedAtMs,
            FirstSpeechTextSentAtMs = firstSpeechTextSentAtMs,
            FirstTextSentAtMs = firstTextSentAtMs,
            ResponsePersistedAtMs = responsePersistedAtMs,
            ServerCompletedAtMs = turnTimeline.ElapsedMilliseconds,
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

    // Progress is deliberately voice-only: it keeps a slow grounded lookup
    // conversational without inserting system chatter into the saved reply.
    private async Task EmitVoiceWebSearchProgressAsync(
        Task agentCompleted,
        Guid? clientTurnId,
        Stopwatch timeline,
        Action onProgressSent,
        CancellationToken cancellationToken)
    {
        var updates = new (TimeSpan Delay, string Text)[]
        {
            (TimeSpan.FromSeconds(3.5), "Ищу свежие сведения."),
            (TimeSpan.FromSeconds(5), "Проверяю источники.")
        };

        try
        {
            for (var index = 0; index < updates.Length; index++)
            {
                var update = updates[index];
                var delayTask = Task.Delay(update.Delay, cancellationToken);
                if (await Task.WhenAny(agentCompleted, delayTask) == agentCompleted)
                    return;

                await delayTask;
                if (agentCompleted.IsCompleted)
                    return;

                var progressData = JsonSerializer.Serialize(new { progressText = update.Text });
                await Response.WriteAsync($"data: {progressData}\n\n", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
                onProgressSent();
                _logger.LogInformation(
                    "Voice turn web search progress sent: Turn {ClientTurnId}; At {ElapsedMs}ms; Index {ProgressIndex}",
                    clientTurnId,
                    timeline.ElapsedMilliseconds,
                    index + 1);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client ended the turn; normal request cancellation owns cleanup.
        }
    }

    private static async IAsyncEnumerable<string> StreamSingleResponseAsync(string response)
    {
        await Task.CompletedTask;
        yield return response;
    }

    public static bool LooksLikeInternalProtocolReply(string? text) =>
        !string.IsNullOrWhiteSpace(text) && InternalProtocolReplyPattern.IsMatch(text);

    internal static bool TryGetWebSearchQuery(AssistantToolExecution execution, out string query)
    {
        query = string.Empty;
        if (execution.Name != "web_search" || execution.Data is not { ValueKind: JsonValueKind.Object } data ||
            !data.TryGetProperty("query", out var queryValue) || queryValue.ValueKind != JsonValueKind.String)
            return false;

        query = queryValue.GetString()?.Trim() ?? string.Empty;
        return query.Length > 0;
    }

    public static string BuildReminderReceiptReply(ReminderDraft reminder, string deliveryStatus) =>
        deliveryStatus switch
        {
            ReminderDeliveryStatuses.Scheduled when reminder.IsPeriodic =>
                $"Повторяющееся напоминание «{reminder.Text}» установлено на телефоне. Оно будет работать без интернета.",
            ReminderDeliveryStatuses.Scheduled =>
                $"Напоминание «{reminder.Text}» установлено на телефоне. Оно сработает без интернета.",
            ReminderDeliveryStatuses.Failed =>
                $"Телефон пока не смог установить напоминание «{reminder.Text}». Проверь уведомления и попробуй ещё раз.",
            _ =>
                $"Напоминание «{reminder.Text}» сохранено, но телефон пока не подтвердил установку. Проверь уведомления и попробуй ещё раз."
        };

    private static string GetSafeToolFallback(IReadOnlyList<AssistantToolExecution> executions, ExternalActionCommand? externalAction)
    {
        if (externalAction is not null)
            return GetExternalActionFallback(externalAction.Type);

        var lastExecution = executions.LastOrDefault();
        return lastExecution?.Status is "created" or "ok" or "cancelled" or "requested"
            ? "Готово. Я выполнила этот запрос."
            : "Не получилось корректно завершить это действие. Давай попробуем ещё раз.";
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

        return Ok(new { fileName, sizeBytes = file.Length });
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
            Если да — выведи одну нейтральную реплику ожидания на 2-7 слов, без кавычек. Это не начало ответа и не план: она может лишь мягко отразить тему сообщения (например "Про это — секунду." или "Понял, минутку."), но не должна отвечать по существу, обещать действие или результат. Не используй "сделаю", "найду", "открою", "запишу", "проверю" и другие формулировки, предвосхищающие дальнейший ответ.
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
        return AudioCoreTranscriptionService.NormalizePreamble(result);
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
