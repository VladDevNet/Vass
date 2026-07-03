using System.Net.Http.Headers;
using System.Text.Json;

namespace VoiceAssistant.API.Services;

public class PronunciationService
{
    private readonly string _defaultApiKey;
    private readonly ILogger<PronunciationService> _logger;

    public PronunciationService(IConfiguration config, ILogger<PronunciationService> logger)
    {
        _defaultApiKey = config["OpenAI:ApiKey"]!;
        _logger = logger;
    }

    public async Task<PronunciationResult?> AnalyzeAsync(string wavPath, string transcription, string? apiKey = null)
    {
        var key = string.IsNullOrWhiteSpace(apiKey) ? _defaultApiKey : apiKey;
        var audioBytes = await File.ReadAllBytesAsync(wavPath);
        var base64Audio = Convert.ToBase64String(audioBytes);
        var ext = Path.GetExtension(wavPath).TrimStart('.'); // wav or mp3

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        http.Timeout = TimeSpan.FromSeconds(30);

        var payload = new
        {
            model = "gpt-4o-audio-preview",
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "input_audio",
                            input_audio = new { data = base64Audio, format = ext }
                        },
                        new
                        {
                            type = "text",
                            text = $$"""
                                Uczeń próbował powiedzieć po polsku: "{{transcription}}"
                                Oceń wymowę: akcent, intonację, poprawność dźwięków.
                                Odpowiedz TYLKO w JSON:
                                {"accuracy": 1-10, "feedback": "krótki komentarz po ukraińsku", "problemWords": [{"word": "...", "issue": "problem po ukraińsku"}]}
                                Jeśli wymowa jest dobra, problemWords może być puste.
                                """
                        }
                    }
                }
            },
            max_tokens = 300
        };

        try
        {
            var response = await http.PostAsync("https://api.openai.com/v1/chat/completions",
                new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json"));

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Pronunciation API error {Status}: {Body}", response.StatusCode, err);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";

            // Extract JSON from response (may be wrapped in markdown code block)
            var jsonStart = content.IndexOf('{');
            var jsonEnd = content.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd < 0) return null;

            var resultJson = content[jsonStart..(jsonEnd + 1)];
            return JsonSerializer.Deserialize<PronunciationResult>(resultJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pronunciation analysis failed");
            return null;
        }
    }
}

public class PronunciationResult
{
    public int Accuracy { get; set; }
    public string Feedback { get; set; } = "";
    public List<ProblemWord> ProblemWords { get; set; } = [];
}

public class ProblemWord
{
    public string Word { get; set; } = "";
    public string Issue { get; set; } = "";
}
