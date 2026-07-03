using System.Text.Json;

namespace PolishTutor.Api.Services;

public class AudioAnalysisService
{
    private readonly string _apiKey;
    private readonly ILogger<AudioAnalysisService> _logger;
    private const string Model = "gemini-2.5-flash";

    public AudioAnalysisService(IConfiguration config, ILogger<AudioAnalysisService> logger)
    {
        _apiKey = config["Gemini:ApiKey"]!;
        _logger = logger;
    }

    public async Task<AudioAnalysisResult?> TranscribeAndAnalyzeAsync(string audioPath, string? apiKey = null)
    {
        var key = string.IsNullOrWhiteSpace(apiKey) ? _apiKey : apiKey;
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

        using var http = new HttpClient();
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
                                To jest nagranie ucznia, który uczy się języka polskiego. Jego język ojczysty to ukraiński/rosyjski.
                                WAŻNE: Uczeń mówi PO POLSKU (może z błędami i akcentem). To NIE jest białoruski, ukraiński ani żaden inny język — to próba mówienia po polsku.

                                Zrób dwie rzeczy:
                                1. Transkrybuj DOKŁADNIE to, co uczeń powiedział — zapisz po polsku (nawet jeśli wymowa jest niedoskonała)
                                2. Oceń wymowę: akcent, intonację, poprawność dźwięków

                                Odpowiedz TYLKO czystym JSON (bez markdown, bez ```):
                                {"transcription": "tekst po polsku", "accuracy": 1-10, "feedback": "krótki komentarz po ukraińsku", "problemWords": [{"word": "słowo", "issue": "problem po ukraińsku"}]}
                                Jeśli wymowa jest dobra, problemWords może być puste [].
                                Jeśli nagranie jest ciche lub niezrozumiałe, zwróć: {"transcription": "", "accuracy": 0, "feedback": "Не вдалося розпізнати мову", "problemWords": []}
                                """
                        }
                    }
                }
            },
            generationConfig = new
            {
                maxOutputTokens = 1024,
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
            // Concatenate text from all parts
            var parts = doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts");
            var content = string.Join("", parts.EnumerateArray()
                .Where(p => p.TryGetProperty("text", out _))
                .Select(p => p.GetProperty("text").GetString() ?? ""));

            // Strip markdown code fences if present
            var clean = content.Trim();
            if (clean.StartsWith("```"))
            {
                clean = clean[clean.IndexOf('\n')..];
                var endFence = clean.LastIndexOf("```");
                if (endFence >= 0) clean = clean[..endFence];
                clean = clean.Trim();
            }

            // Extract JSON from response
            var jsonStart = clean.IndexOf('{');
            var jsonEnd = clean.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd < 0)
            {
                _logger.LogWarning("No JSON found in Gemini response: {Content}", content);
                return null;
            }

            var resultJson = clean[jsonStart..(jsonEnd + 1)];
            return JsonSerializer.Deserialize<AudioAnalysisResult>(resultJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gemini audio analysis failed");
            return null;
        }
    }
}

public class AudioAnalysisResult
{
    public string Transcription { get; set; } = "";
    public int Accuracy { get; set; }
    public string Feedback { get; set; } = "";
    public List<ProblemWord> ProblemWords { get; set; } = [];
}
