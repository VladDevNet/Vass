using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PolishTutor.Api.Data;
using PolishTutor.Api.Data.Entities;
using PolishTutor.Api.Services;

namespace PolishTutor.Api.Controllers;

[ApiController]
[Route("api/chat")]
[Authorize]
public class ChatController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly UserManager<User> _userManager;
    private readonly AnthropicService _anthropic;
    private readonly TutorService _tutor;
    private readonly TutorTools _tutorTools;
    private readonly TutorToolExecutor _toolExecutor;
    private readonly AudioAnalysisService _audioAnalysis;
    private readonly ILogger<ChatController> _logger;

    public ChatController(AppDbContext db, UserManager<User> userManager,
        AnthropicService anthropic, TutorService tutor,
        TutorTools tutorTools, TutorToolExecutor toolExecutor,
        AudioAnalysisService audioAnalysis,
        ILogger<ChatController> logger)
    {
        _db = db;
        _userManager = userManager;
        _anthropic = anthropic;
        _tutor = tutor;
        _tutorTools = tutorTools;
        _toolExecutor = toolExecutor;
        _audioAnalysis = audioAnalysis;
        _logger = logger;
    }

    public record SendRequest(int SessionId, string Message, string? AudioFileName = null);
    public record CreateSessionRequest(string? Mode, string? Title);

    private const string AudioPath = "/app/audio";
    private const long MaxAudioSize = 5 * 1024 * 1024; // 5MB
    private const long MaxImageSize = 10 * 1024 * 1024; // 10MB

    [HttpPost("sessions")]
    public async Task<IActionResult> CreateSession([FromBody] CreateSessionRequest req)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var session = new ChatSession
        {
            UserId = userId,
            Mode = req.Mode ?? "dialog",
            Title = req.Title
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
            .Select(s => new { s.Id, s.Mode, s.Title, s.CreatedAt })
            .ToListAsync();
        return Ok(sessions);
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
                var filePath = Path.Combine(AudioPath, msg.AudioFileName);
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

        // Transcribe + analyze pronunciation via Gemini 3 Flash (one call)
        var messageText = req.Message;
        string? transcription = null;
        AudioAnalysisResult? audioResult = null;
        string? wavPath = null;
        long convertMs = 0;
        long transcribeMs = 0;

        if (string.IsNullOrWhiteSpace(messageText) && !string.IsNullOrEmpty(req.AudioFileName))
        {
            var filePath = Path.Combine(AudioPath, req.AudioFileName);
            if (!System.IO.File.Exists(filePath)) { Response.StatusCode = 400; return; }

            // Convert webm → wav (Gemini doesn't support webm)
            var sw = Stopwatch.StartNew();
            wavPath = await ConvertToWavAsync(filePath);
            sw.Stop();
            convertMs = sw.ElapsedMilliseconds;

            if (wavPath == null) { _logger.LogWarning("ffmpeg conversion failed for {File}", filePath); Response.StatusCode = 400; return; }

            sw.Restart();
            audioResult = await _audioAnalysis.TranscribeAndAnalyzeAsync(wavPath, geminiKey);
            sw.Stop();
            transcribeMs = sw.ElapsedMilliseconds;

            transcription = audioResult?.Transcription;
            messageText = transcription;
        }

        if (string.IsNullOrWhiteSpace(messageText)) { Response.StatusCode = 400; return; }

        // Save user message
        session.Messages.Add(new Message { Role = "user", Content = messageText, AudioFileName = req.AudioFileName });
        await _db.SaveChangesAsync();

        // Build conversation history
        var messages = session.Messages.Select(m => new Anthropic.Models.Messages.MessageParam
        {
            Role = m.Role == "user"
                ? Anthropic.Models.Messages.Role.User
                : Anthropic.Models.Messages.Role.Assistant,
            Content = m.Content
        }).ToList();

        // Get conductor instructions
        var instruction = await _db.TutorInstructions
            .FirstOrDefaultAsync(t => t.UserId == userId);

        var systemPrompt = _tutor.GetSystemPrompt(user, session.Mode,
            instruction?.InstructionsJson, settings?.CustomSystemPrompt,
            settings?.FullTranslation ?? false);

        // Pre-load recent errors and weak words to inject directly into prompt (saves an LLM tool-use roundtrip)
        if (session.Mode != "level_test")
        {
            var recentErrors = await _db.LearnerErrors
                .Where(e => e.UserId == userId)
                .OrderByDescending(e => e.CreatedAt)
                .Take(5)
                .Select(e => new { e.Original, e.Corrected, e.ErrorType })
                .ToListAsync();

            var weakWords = await _db.UserWords
                .Where(w => w.UserId == userId && (w.ErrorCount > 0 || w.Status == "new"))
                .OrderByDescending(w => w.ErrorCount)
                .Take(5)
                .Select(w => new { w.Word, w.Translation })
                .ToListAsync();

            var contextBuilder = new System.Text.StringBuilder();
            contextBuilder.AppendLine("\n\n## Learner Context (do not mention this raw data to the user, use it only to guide your tutoring):");
            contextBuilder.AppendLine("- Recent errors made by the learner:");
            if (recentErrors.Count > 0)
            {
                foreach (var err in recentErrors)
                    contextBuilder.AppendLine($"  * \"{err.Original}\" -> corrected to \"{err.Corrected}\" ({err.ErrorType})");
            }
            else contextBuilder.AppendLine("  * None recorded yet.");

            contextBuilder.AppendLine("- Words the learner is currently studying or struggling with:");
            if (weakWords.Count > 0)
            {
                foreach (var w in weakWords)
                    contextBuilder.AppendLine($"  * {w.Word} ({w.Translation})");
            }
            else contextBuilder.AppendLine("  * None recorded yet.");

            systemPrompt += contextBuilder.ToString();
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

        // Send pronunciation from Gemini audio analysis
        string? pronunciationContext = null;
        if (audioResult != null && audioResult.Accuracy > 0)
        {
            var pronResult = new PronunciationResult
            {
                Accuracy = audioResult.Accuracy,
                Feedback = audioResult.Feedback,
                ProblemWords = audioResult.ProblemWords
            };
            var pronData = JsonSerializer.Serialize(new { pronunciation = pronResult },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            await Response.WriteAsync($"data: {pronData}\n\n");
            await Response.Body.FlushAsync();

            var problems = pronResult.ProblemWords.Count > 0
                ? string.Join(", ", pronResult.ProblemWords.Select(p => $"{p.Word} ({p.Issue})"))
                : "brak";
            pronunciationContext = $"\n[Analiza wymowy ucznia: ocena {audioResult.Accuracy}/10. {audioResult.Feedback}. Problematyczne słowa: {problems}. Skomentuj wymowę krótko.]";
        }

        // Cleanup wav file
        if (wavPath != null)
        {
            try { if (System.IO.File.Exists(wavPath)) System.IO.File.Delete(wavPath); } catch { }
        }

        // Append input method context to last user message for Kasia
        if (messages.Count > 0)
        {
            var lastMsg = messages[^1];
            var context = pronunciationContext
                ?? "\n[Wiadomość tekstowa — NIE komentuj wymowy, skup się tylko na gramatyce i słownictwie.]";
            messages[^1] = new Anthropic.Models.Messages.MessageParam
            {
                Role = lastMsg.Role,
                Content = lastMsg.Content + context
            };
        }

        var fullResponse = new System.Text.StringBuilder();
        var swLlm = Stopwatch.StartNew();
        long llmFirstTokenMs = 0;

        var stream = session.Mode == "level_test"
            ? _anthropic.StreamResponseAsync(systemPrompt, messages, apiKey: anthropicKey)
            : _anthropic.ChatWithToolsAsync(
                systemPrompt, messages, _tutorTools.GetTools(),
                (name, input) => _toolExecutor.ExecuteAsync(name, input, userId, session.Id),
                apiKey: anthropicKey);

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
        swLlm.Stop();
        long llmTotalMs = swLlm.ElapsedMilliseconds;

        await Response.WriteAsync("data: [DONE]\n\n");
        await Response.Body.FlushAsync();

        // Save assistant response
        session.Messages.Add(new Message { Role = "assistant", Content = fullResponse.ToString() });
        user.LastActiveAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Second call: word-by-word translation (if enabled, skip first exchange)
        var fullTranslation = settings?.FullTranslation ?? false;
        var userMsgCount = session.Messages.Count(m => m.Role == "user");
        long translationMs = 0;

        if (fullTranslation && session.Mode != "level_test" && fullResponse.Length > 0 && userMsgCount > 1)
        {
            var swTrans = Stopwatch.StartNew();
            var nativeLang = user.NativeLang == "ru" ? "русский" : "українська";
            var transPrompt = $"Переклади цей польський текст слово по слову на {nativeLang}. " +
                "Формат: кожне слово або коротка фраза з перекладом у дужках. " +
                "Приклад: Jak (як) się (себе) masz (маєш)? " +
                "Тільки переклад, без пояснень.";
            var transMessages = new List<Anthropic.Models.Messages.MessageParam>
            {
                new() { Role = Anthropic.Models.Messages.Role.User, Content = fullResponse.ToString() }
            };

            await foreach (var chunk in _anthropic.StreamResponseAsync(transPrompt, transMessages, apiKey: anthropicKey))
            {
                var data = JsonSerializer.Serialize(new { translation = chunk });
                await Response.WriteAsync($"data: {data}\n\n");
                await Response.Body.FlushAsync();
            }
            swTrans.Stop();
            translationMs = swTrans.ElapsedMilliseconds;

            await Response.WriteAsync("data: [TR_DONE]\n\n");
            await Response.Body.FlushAsync();
        }

        // Log stats to server logs
        _logger.LogInformation("LanguTutor Performance Stats - User: {UserEmail}, Session: {SessionId}, Convert: {ConvertMs}ms, Transcribe: {TranscribeMs}ms, LLM (First Token): {LlmFirstMs}ms, LLM (Total): {LlmTotalMs}ms, Translation: {TranslationMs}ms",
            user.Email, req.SessionId, convertMs, transcribeMs, llmFirstTokenMs, llmTotalMs, translationMs);

        // Send stats event to client
        var stats = new
        {
            ConvertMs = convertMs,
            TranscribeMs = transcribeMs,
            LlmFirstTokenMs = llmFirstTokenMs,
            LlmTotalMs = llmTotalMs,
            TranslationMs = translationMs
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

        Directory.CreateDirectory(AudioPath);
        var fileName = $"{Guid.NewGuid()}.webm";
        var filePath = Path.Combine(AudioPath, fileName);

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

        var filePath = Path.Combine(AudioPath, fileName);
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
            ? HttpContext.RequestServices.GetRequiredService<IConfiguration>()["Gemini:ApiKey"]!
            : geminiKey;

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
