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
}
