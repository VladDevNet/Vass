using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
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
    private readonly UserManager<User> _userManager;
    private readonly GeminiService _gemini;
    private readonly CompanionPromptService _tutor;
    private readonly AudioAnalysisService _audioAnalysis;
    private readonly SpeakerRegistryService _speakerRegistry;
    private readonly ConversationMemoryService _conversationMemory;
    private readonly IConfiguration _config;
    private readonly ILogger<ChatController> _logger;
    private readonly string _audioPath;

    private readonly PiperTtsService _ttsService;

    public ChatController(AppDbContext db, UserManager<User> userManager,
        GeminiService gemini, CompanionPromptService tutor,
        AudioAnalysisService audioAnalysis, SpeakerRegistryService speakerRegistry, ConversationMemoryService conversationMemory,
        PiperTtsService ttsService,
        IConfiguration config, IWebHostEnvironment env, ILogger<ChatController> logger)
    {
        _db = db;
        _userManager = userManager;
        _gemini = gemini;
        _tutor = tutor;
        _audioAnalysis = audioAnalysis;
        _speakerRegistry = speakerRegistry;
        _conversationMemory = conversationMemory;
        _ttsService = ttsService;
        _config = config;
        _logger = logger;

        var configuredAudioPath = config["Audio:Path"];
        var audioPath = string.IsNullOrWhiteSpace(configuredAudioPath)
            ? Path.Combine(env.ContentRootPath, "audio")
            : configuredAudioPath;
        _audioPath = Path.GetFullPath(Path.IsPathRooted(audioPath)
            ? audioPath
            : Path.Combine(env.ContentRootPath, audioPath));
    }

    public record SendRequest(int SessionId, string Message, string? AudioFileName = null);
    public record CreateSessionRequest(string? Mode, string? Title);

    private const long MaxAudioSize = 5 * 1024 * 1024; // 5MB
    private const long MaxImageSize = 10 * 1024 * 1024; // 10MB

    public record TtsRequest(string Text, string? Voice = null);

    [HttpPost("tts")]
    public async Task<IActionResult> GenerateTts([FromBody] TtsRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Text)) return BadRequest();

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

    [HttpPost("sessions")]
    public async Task<IActionResult> CreateSession([FromBody] CreateSessionRequest req)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var existing = await _db.ChatSessions
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync();

        if (existing != null)
        {
            return Ok(new { existing.Id, Mode = "dialog", Title = "Общение", existing.CreatedAt });
        }

        var session = new ChatSession
        {
            UserId = userId,
            Mode = "dialog",
            Title = "Общение"
        };
        _db.ChatSessions.Add(session);
        await _db.SaveChangesAsync();

        return Ok(new { session.Id, session.Mode, session.Title, session.CreatedAt });
    }

    [HttpGet("sessions")]
    public async Task<IActionResult> GetSessions()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var sessions = await _db.ChatSessions
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();

        if (sessions.Count == 0)
        {
            var session = new ChatSession
            {
                UserId = userId,
                Mode = "dialog",
                Title = "Общение"
            };
            _db.ChatSessions.Add(session);
            await _db.SaveChangesAsync();
            sessions.Add(session);
        }

        var singleSession = sessions[0];
        return Ok(new[] { new { singleSession.Id, Mode = "dialog", Title = "Общение", singleSession.CreatedAt } });
    }

    public record RenameSessionRequest(string Title);

    [HttpPatch("sessions/{id:int}")]
    public async Task<IActionResult> RenameSession(int id, [FromBody] RenameSessionRequest req)
    {
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

        foreach (var msg in session.Messages)
        {
            if (!string.IsNullOrEmpty(msg.AudioFileName))
            {
                var filePath = Path.Combine(_audioPath, msg.AudioFileName);
                if (System.IO.File.Exists(filePath))
                    System.IO.File.Delete(filePath);
            }
        }

        _db.ChatSessions.Remove(session);
        await _db.SaveChangesAsync();
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

        var page = await query.OrderByDescending(m => m.Id).Take(limit).ToListAsync();
        page.Reverse(); // chronological order, matching the non-paginated response this replaces

        return Ok(new
        {
            session.Id,
            session.Mode,
            session.Title,
            Messages = page.Select(m => new { m.Id, m.Role, m.Content, m.CreatedAt, m.AudioFileName }),
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

        if (string.IsNullOrWhiteSpace(messageText) && !string.IsNullOrEmpty(req.AudioFileName))
        {
            attemptedTranscription = true;
            var filePath = Path.Combine(_audioPath, req.AudioFileName);
            if (!System.IO.File.Exists(filePath)) { Response.StatusCode = 400; return; }

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

            // Speaker identification paused: real short phone-mic clips scored too close
            // to noise-floor similarity in testing (~0.18-0.28, near the ~0.16 seen between
            // different synthetic voices) to trust yet, and it costs 400ms-2s per turn for
            // an unproven payoff. Infrastructure (service, DB table, matching logic) is
            // still in place — flip this back on once it's worth revisiting.
            // if (!string.IsNullOrWhiteSpace(transcription))
            // {
            //     sw.Restart();
            //     speakerResult = await _speakerRegistry.IdentifyAsync(wavPath, transcription, geminiKey);
            //     sw.Stop();
            //     speakerIdMs = sw.ElapsedMilliseconds;
            // }
        }

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

        // Save user message
        session.Messages.Add(new Message { Role = "user", Content = messageText, AudioFileName = req.AudioFileName });
        await _db.SaveChangesAsync();

        // Check (in parallel, doesn't block the response) whether the user asked the
        // assistant to remember a persistent behavior preference ("говори медленнее",
        // "давай на ты", etc.) so future turns' system prompts reflect it. Awaited at
        // the very end of this action — the client already moves on once it sees
        // [DONE], so this adds no perceived latency.
        var instructionUpdateTask = MaybeUpdateCustomInstructionsAsync(userId, messageText, settings?.CustomSystemPrompt, geminiKey, HttpContext.RequestAborted);

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
        foreach (var m in session.Messages.OrderByDescending(m => m.CreatedAt).Take(MaxCandidateMessages))
        {
            if (windowed.Count > 0 && usedChars + m.Content.Length > HistoryCharBudget)
            {
                break;
            }
            windowed.Add(m);
            usedChars += m.Content.Length;
        }
        windowed.Reverse(); // back to chronological order for the API

        var messages = windowed.Select(m => new GeminiMessage(
            m.Role == "user" ? "user" : "assistant",
            m.Content
        )).ToList();

        var systemPrompt = _tutor.GetSystemPrompt(settings?.CustomSystemPrompt, settings?.DisplayName, settings?.AssistantName, session.MediumTermSummary);

        if (speakerResult?.KnownName != null)
        {
            systemPrompt = $"Сейчас с тобой говорит: {speakerResult.KnownName}. Обращайся к нему соответственно, если это уместно.\n\n{systemPrompt}";
        }
        else if (speakerResult?.ShouldAskForName == true)
        {
            systemPrompt = $"Ты слышишь новый, ранее не знакомый тебе голос несколько реплик подряд. Ненавязчиво, как естественную часть своего ответа по теме разговора, поинтересуйся как зовут собеседника — не делай это отдельным резким вопросом или объявлением о распознавании голоса.\n\n{systemPrompt}";
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

        // Cleanup wav file
        if (wavPath != null)
        {
            try { if (System.IO.File.Exists(wavPath)) System.IO.File.Delete(wavPath); } catch { }
        }

        var fullResponse = new System.Text.StringBuilder();
        var swLlm = Stopwatch.StartNew();
        long llmFirstTokenMs = 0;

        // Kick off a fast, non-grounded "will this need search/deep thought?" check
        // in parallel with the real (possibly slow, search-grounded) response, so we
        // can speak a natural "hold on" phrase while the real answer is still cooking
        // instead of leaving the user in silence for several seconds.
        var preambleTask = GetPreambleIfNeededAsync(messageText, geminiKey, HttpContext.RequestAborted);

        // Use Gemini 3.5 Flash with Google Search grounding for real-time facts (news, weather, etc.)
        var stream = _gemini.StreamResponseAsync(systemPrompt, messages, model: "gemini-3.5-flash", apiKey: geminiKey, cancellationToken: HttpContext.RequestAborted);
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
        catch (OperationCanceledException)
        {
            // Client abandoned this turn (e.g. user kept talking) — stop without
            // saving a partial/incomplete assistant reply. The user's message stays saved.
            _logger.LogInformation("Chat stream cancelled by client for session {SessionId}", req.SessionId);
            await AwaitInstructionUpdateAsync(instructionUpdateTask);
            return;
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
        swLlm.Stop();
        long llmTotalMs = swLlm.ElapsedMilliseconds;

        await Response.WriteAsync("data: [DONE]\n\n");
        await Response.Body.FlushAsync();

        // Save assistant response
        session.Messages.Add(new Message { Role = "assistant", Content = fullResponse.ToString() });
        user.LastActiveAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await AwaitInstructionUpdateAsync(instructionUpdateTask);
        await _conversationMemory.CheckAndUpdateAsync(session, geminiKey, HttpContext.RequestAborted);

        // Log stats to server logs
        _logger.LogInformation("VoiceAssistant Performance Stats - User: {UserEmail}, Session: {SessionId}, Convert: {ConvertMs}ms, Transcribe: {TranscribeMs}ms, SpeakerId: {SpeakerIdMs}ms, LLM (First Token): {LlmFirstMs}ms, LLM (Total): {LlmTotalMs}ms",
            user.Email, req.SessionId, convertMs, transcribeMs, speakerIdMs, llmFirstTokenMs, llmTotalMs);

        // Send stats event to client
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
    }

    [HttpPost("upload-audio")]
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

        if (!file.ContentType.StartsWith("image/"))
            return BadRequest(new { error = "Only image files are allowed" });

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var settings = await _db.UserSettings.FirstOrDefaultAsync(s => s.UserId == userId);
        var geminiKey = settings?.GeminiApiKey;
        var key = string.IsNullOrWhiteSpace(geminiKey)
            ? _config["Gemini:ApiKey"]
            : geminiKey;
        if (string.IsNullOrWhiteSpace(key))
            return BadRequest(new { error = "Gemini API key is not configured" });

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var base64 = Convert.ToBase64String(ms.ToArray());

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
                        new { inline_data = new { mime_type = file.ContentType, data = base64 } },
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
                maxTokens: 30, apiKey: geminiKey, enableGrounding: false, cancellationToken: ct))
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

    private async Task AwaitInstructionUpdateAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Custom-instruction update failed");
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
                maxTokens: 200, apiKey: geminiKey, enableGrounding: false, cancellationToken: ct))
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

        var settings = await _db.UserSettings.FirstOrDefaultAsync(s => s.UserId == userId);
        if (settings == null)
        {
            settings = new Data.Entities.UserSettings { UserId = userId, CustomSystemPrompt = result };
            _db.UserSettings.Add(settings);
        }
        else
        {
            settings.CustomSystemPrompt = result;
            settings.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
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
