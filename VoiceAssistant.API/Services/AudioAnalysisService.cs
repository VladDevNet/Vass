using System.Text.Json;

namespace VoiceAssistant.API.Services;

public class AudioAnalysisService
{
    private readonly string _apiKey;
    private readonly ILogger<AudioAnalysisService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private const string Model = "gemini-2.5-flash";

    public AudioAnalysisService(IConfiguration config, ILogger<AudioAnalysisService> logger, IHttpClientFactory httpClientFactory)
    {
        _apiKey = config["Gemini:ApiKey"]!;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<string?> TranscribeAsync(string audioPath, string? apiKey = null)
    {
        var key = string.IsNullOrWhiteSpace(apiKey) ? _apiKey : apiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            _logger.LogWarning("Gemini API key is missing. Cannot transcribe audio.");
            return null;
        }

        var audioBytes = await File.ReadAllBytesAsync(audioPath);
        var base64Audio = Convert.ToBase64String(audioBytes);
        var ext = Path.GetExtension(audioPath).TrimStart('.'); // wav, mp3, ogg
        var mimeType = ext switch
        {
            "wav" => "audio/wav",
            "mp3" => "audio/mp3",
            "ogg" => "audio/ogg",
            "flac" => "audio/flac",
            _ => "audio/wav"
        };

        using var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(45);

        var payload = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new
                        {
                            inline_data = new { mime_type = mimeType, data = base64Audio }
                        },
                        new
                        {
                            text = """
                                Это аудиозапись речи пользователя, говорящего на русском или украинском языке.
                                Сделай точную транскрипцию того, что пользователь сказал в записи. Убери посторонние вздохи, шумы или щелчки, оставив чистый текст.
                                Ответь ТОЛЬКО текстом транскрипции, без кавычек и пояснений. Если запись пустая, тихая или не содержит речи, верни пустую строку.
                                """
                        }
                    }
                }
            },
            generationConfig = new
            {
                maxOutputTokens = 256,
                thinkingConfig = new { thinkingBudget = 0 }
            }
        };

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Model}:generateContent?key={key}";

        try
        {
            var response = await http.PostAsync(url,
                new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json"));

            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Gemini API error {Status}: {Body}", response.StatusCode, body);
                return null;
            }

            var doc = JsonDocument.Parse(body);
            var parts = doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts");
            var content = string.Join("", parts.EnumerateArray()
                .Where(p => p.TryGetProperty("text", out _))
                .Select(p => p.GetProperty("text").GetString() ?? ""));

            return content.Trim().Trim('"');
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gemini transcription failed");
            return null;
        }
    }

    // Transcribes a (possibly not-yet-finished) recording snapshot AND judges whether
    // the speaker sounds done with their thought (ready for a response) or is likely
    // still talking/pausing to think. Used to give a real conversational partner's
    // patience instead of cutting people off on a fixed short silence timeout.
    public async Task<UtteranceCheckResult?> CheckUtteranceCompletionAsync(string audioPath, string? apiKey = null)
    {
        var key = string.IsNullOrWhiteSpace(apiKey) ? _apiKey : apiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            _logger.LogWarning("Gemini API key is missing. Cannot check utterance completion.");
            return null;
        }

        var audioBytes = await File.ReadAllBytesAsync(audioPath);
        var base64Audio = Convert.ToBase64String(audioBytes);
        var ext = Path.GetExtension(audioPath).TrimStart('.');
        var mimeType = ext switch
        {
            "wav" => "audio/wav",
            "mp3" => "audio/mp3",
            "ogg" => "audio/ogg",
            "flac" => "audio/flac",
            _ => "audio/wav"
        };

        using var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(45);

        var payload = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { inline_data = new { mime_type = mimeType, data = base64Audio } },
                        new
                        {
                            text = """
                                Это аудиозапись человека, разговаривающего с голосовым ассистентом. Он мог уже
                                закончить свою мысль и ждать ответа, а мог просто взять паузу, чтобы подумать,
                                и собирается продолжить говорить.
                                Сделай точную транскрипцию того, что было сказано (без вздохов/шумов).
                                Затем оцени: похоже ли, что человек закончил (законченный вопрос, просьба, явная
                                пауза для ответа) — или он, вероятно, продолжит (оборванная фраза, слово-паразит
                                в конце вроде "ну", "вот", "это", "так", явно неполная мысль).
                                Ответь СТРОГО в формате JSON без markdown:
                                {"transcription": "текст", "complete": true или false}
                                Если записи не слышно/тишина, верни {"transcription": "", "complete": false}.
                                """
                        }
                    }
                }
            },
            generationConfig = new
            {
                maxOutputTokens = 300,
                thinkingConfig = new { thinkingBudget = 0 }
            }
        };

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Model}:generateContent?key={key}";

        try
        {
            var response = await http.PostAsync(url,
                new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json"));

            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Gemini API error {Status}: {Body}", response.StatusCode, body);
                return null;
            }

            var doc = JsonDocument.Parse(body);
            var parts = doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts");
            var content = string.Join("", parts.EnumerateArray()
                .Where(p => p.TryGetProperty("text", out _))
                .Select(p => p.GetProperty("text").GetString() ?? ""));

            var clean = content.Trim();
            if (clean.StartsWith("```"))
            {
                clean = clean[clean.IndexOf('\n')..];
                var endFence = clean.LastIndexOf("```");
                if (endFence >= 0) clean = clean[..endFence];
                clean = clean.Trim();
            }

            var jsonStart = clean.IndexOf('{');
            var jsonEnd = clean.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd < 0)
            {
                _logger.LogWarning("No JSON found in completion-check response: {Content}", content);
                return null;
            }

            var resultJson = clean[jsonStart..(jsonEnd + 1)];
            return JsonSerializer.Deserialize<UtteranceCheckResult>(resultJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gemini utterance-completion check failed");
            return null;
        }
    }
}

public class UtteranceCheckResult
{
    public string Transcription { get; set; } = "";
    public bool Complete { get; set; }
}
