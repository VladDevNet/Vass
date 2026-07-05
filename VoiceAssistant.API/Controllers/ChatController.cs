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
[Route("api/chat")]
[Authorize]
public class ChatController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly UserManager<User> _userManager;
    private readonly AnthropicService _anthropic;
    private readonly GeminiService _gemini;
    private readonly OpenAiChatService _openAiChat;
    private readonly TutorService _tutor;
    private readonly TutorTools _tutorTools;
    private readonly TutorToolExecutor _toolExecutor;
    private readonly AudioAnalysisService _audioAnalysis;
    private readonly SpeakerRegistryService _speakerRegistry;
    private readonly IConfiguration _config;
    private readonly ILogger<ChatController> _logger;
    private readonly string _audioPath;

    private readonly PiperTtsService _ttsService;

    public ChatController(AppDbContext db, UserManager<User> userManager,
        AnthropicService anthropic, GeminiService gemini, OpenAiChatService openAiChat, TutorService tutor,
        TutorTools tutorTools, TutorToolExecutor toolExecutor,
        AudioAnalysisService audioAnalysis, SpeakerRegistryService speakerRegistry, PiperTtsService ttsService,
        IConfiguration config, IWebHostEnvironment env, ILogger<ChatController> logger)
    {
        _db = db;
        _userManager = userManager;
        _anthropic = anthropic;
        _gemini = gemini;
        _openAiChat = openAiChat;
        _tutor = tutor;
        _tutorTools = tutorTools;
        _toolExecutor = toolExecutor;
        _audioAnalysis = audioAnalysis;
        _speakerRegistry = speakerRegistry;
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

    [HttpGet("sessions/{id:int}")]
    public async Task<IActionResult> GetSession(int id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var session = await _db.ChatSessions
            .Include(s => s.Messages.OrderBy(m => m.CreatedAt))
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId);

        if (session == null) return NotFound();

        return Ok(new
        {
            session.Id,
            session.Mode,
            session.Title,
            Messages = session.Messages.Select(m => new { m.Role, m.Content, m.CreatedAt, m.AudioFileName })
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
        var openAiKey = settings?.OpenAiApiKey;
        var anthropicKey = settings?.AnthropicApiKey;
        var geminiKey = settings?.GeminiApiKey;

        var messageText = req.Message;
        string? transcription = null;
        string? wavPath = null;
        long convertMs = 0;
        long transcribeMs = 0;
        long speakerIdMs = 0;
        SpeakerIdResult? speakerResult = null;

        if (string.IsNullOrWhiteSpace(messageText) && !string.IsNullOrEmpty(req.AudioFileName))
        {
            var filePath = Path.Combine(_audioPath, req.AudioFileName);
            if (!System.IO.File.Exists(filePath)) { Response.StatusCode = 400; return; }

            // Convert webm → wav (Gemini doesn't support webm)
            var sw = Stopwatch.StartNew();
            wavPath = await ConvertToWavAsync(filePath);
            sw.Stop();
            convertMs = sw.ElapsedMilliseconds;

            if (wavPath == null) { _logger.LogWarning("ffmpeg conversion failed for {File}", filePath); Response.StatusCode = 400; return; }

            sw.Restart();
            transcription = await _audioAnalysis.TranscribeAsync(wavPath, geminiKey);
            sw.Stop();
            transcribeMs = sw.ElapsedMilliseconds;

            messageText = transcription;

            if (!string.IsNullOrWhiteSpace(transcription))
            {
                sw.Restart();
                speakerResult = await _speakerRegistry.IdentifyAsync(wavPath, transcription, geminiKey);
                sw.Stop();
                speakerIdMs = sw.ElapsedMilliseconds;
            }
        }

        if (string.IsNullOrWhiteSpace(messageText)) { Response.StatusCode = 400; return; }

        // Save user message
        session.Messages.Add(new Message { Role = "user", Content = messageText, AudioFileName = req.AudioFileName });
        await _db.SaveChangesAsync();

        // Build conversation history
        var messages = session.Messages.Select(m => new GeminiMessage(
            m.Role == "user" ? "user" : "assistant",
            m.Content
        )).ToList();

        // Get conductor instructions
        var instruction = await _db.TutorInstructions
            .FirstOrDefaultAsync(t => t.UserId == userId);

        var systemPrompt = _tutor.GetSystemPrompt(user, session.Mode,
            instruction?.InstructionsJson, settings?.CustomSystemPrompt,
            settings?.FullTranslation ?? false);

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

        // Use Gemini 3.5 Flash with Google Search grounding for real-time facts (news, weather, etc.)
        var stream = _gemini.StreamResponseAsync(systemPrompt, messages, model: "gemini-3.5-flash", apiKey: geminiKey, cancellationToken: HttpContext.RequestAborted);

        try
        {
            await foreach (var chunk in stream)
            {
                if (fullResponse.Length == 0)
                {
                    llmFirstTokenMs = swLlm.ElapsedMilliseconds;
                }
                fullResponse.Append(chunk);
                var data = JsonSerializer.Serialize(new { text = chunk });
                await Response.WriteAsync($"data: {data}\n\n");
                await Response.Body.FlushAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Client abandoned this turn (e.g. user kept talking) — stop without
            // saving a partial/incomplete assistant reply. The user's message stays saved.
            _logger.LogInformation("Chat stream cancelled by client for session {SessionId}", req.SessionId);
            return;
        }
        swLlm.Stop();
        long llmTotalMs = swLlm.ElapsedMilliseconds;

        await Response.WriteAsync("data: [DONE]\n\n");
        await Response.Body.FlushAsync();

        // Save assistant response
        session.Messages.Add(new Message { Role = "assistant", Content = fullResponse.ToString() });
        user.LastActiveAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

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
            return BadRequest(new { error = "File must be between 1 byte and 5MB" });

        if (!file.ContentType.StartsWith("audio/"))
            return BadRequest(new { error = "Only audio files are allowed" });

        Directory.CreateDirectory(_audioPath);
        var fileName = $"{Guid.NewGuid()}.webm";
        var filePath = Path.Combine(_audioPath, fileName);

        await using var stream = new FileStream(filePath, FileMode.Create);
        await file.CopyToAsync(stream);

        return Ok(new { fileName });
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
