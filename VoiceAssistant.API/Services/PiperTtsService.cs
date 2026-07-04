using System.Net.Http.Json;

namespace VoiceAssistant.API.Services;

public class PiperTtsService
{
    private readonly string _baseUrl;
    private readonly ILogger<PiperTtsService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public PiperTtsService(IConfiguration config, ILogger<PiperTtsService> logger, IHttpClientFactory httpClientFactory)
    {
        _baseUrl = config["Piper:BaseUrl"] ?? "http://tts:5001";
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<byte[]?> GenerateSpeechAsync(string text)
    {
        using var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);

        try
        {
            var response = await http.PostAsJsonAsync($"{_baseUrl}/synthesize", new { text });
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogError("Piper TTS error {Status}: {Body}", response.StatusCode, body);
                return null;
            }

            return await response.Content.ReadAsByteArrayAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Piper TTS request failed");
            return null;
        }
    }
}
