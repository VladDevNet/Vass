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

    // Used by the readiness endpoint (PROJECT-AUDIT-2026-07-10 REL-03) --
    // deliberately just the service's own /health, not a real synthesis
    // request, so probing readiness doesn't burn TTS compute on every check.
    // Short timeout: a slow-but-alive TTS shouldn't make the whole readiness
    // probe hang.
    public async Task<bool> IsHealthyAsync()
    {
        using var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(3);
        try
        {
            var response = await http.GetAsync($"{_baseUrl}/health");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
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

    // Streams raw 16-bit mono PCM (22050 Hz) straight from Piper into the destination
    // as it's synthesized, instead of buffering the whole utterance first.
    public async Task<bool> StreamSpeechAsync(string text, Stream destination, CancellationToken ct)
    {
        using var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/synthesize_stream")
            {
                Content = JsonContent.Create(new { text })
            };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Piper TTS stream error {Status}", response.StatusCode);
                return false;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await stream.CopyToAsync(destination, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Piper TTS streaming failed");
            return false;
        }
    }
}
