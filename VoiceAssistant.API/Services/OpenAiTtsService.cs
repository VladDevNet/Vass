using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace VoiceAssistant.API.Services;

public class OpenAiTtsService
{
    private readonly string _defaultApiKey;
    private readonly ILogger<OpenAiTtsService> _logger;

    public OpenAiTtsService(IConfiguration config, ILogger<OpenAiTtsService> logger)
    {
        _defaultApiKey = config["OpenAI:ApiKey"] ?? "";
        _logger = logger;
    }

    public async Task<byte[]?> GenerateSpeechAsync(string text, string voice = "nova", string? apiKey = null)
    {
        var key = string.IsNullOrWhiteSpace(apiKey) ? _defaultApiKey : apiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            _logger.LogWarning("OpenAI API key is missing. Cannot generate neural TTS.");
            return null;
        }

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        http.Timeout = TimeSpan.FromSeconds(30);

        var payload = new
        {
            model = "tts-1",
            input = text,
            voice = voice
        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var response = await http.PostAsync("https://api.openai.com/v1/audio/speech", content);
            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync();
                _logger.LogError("OpenAI TTS error {Status}: {Body}", response.StatusCode, errBody);
                return null;
            }

            return await response.Content.ReadAsByteArrayAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenAI TTS generation failed");
            return null;
        }
    }
}
