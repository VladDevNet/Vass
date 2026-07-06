using System.Net.Http.Headers;
using System.Text.Json;

namespace VoiceAssistant.API.Services;

public record SpeakerEmbedding(float[] Vector, bool LowConfidence);

public class SpeakerIdService
{
    private readonly string _baseUrl;
    private readonly ILogger<SpeakerIdService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public SpeakerIdService(IConfiguration config, ILogger<SpeakerIdService> logger, IHttpClientFactory httpClientFactory)
    {
        _baseUrl = config["SpeakerId:BaseUrl"] ?? "http://speaker-id:5003";
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<SpeakerEmbedding?> GetEmbeddingAsync(string wavPath)
    {
        using var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(20);

        try
        {
            using var content = new MultipartFormDataContent();
            var bytes = await File.ReadAllBytesAsync(wavPath);
            var fileContent = new ByteArrayContent(bytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(fileContent, "audio", "audio.wav");

            var response = await http.PostAsync($"{_baseUrl}/embed", content);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogError("Speaker-id embed error {Status}: {Body}", response.StatusCode, body);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement.GetProperty("embedding");
            var vector = new float[arr.GetArrayLength()];
            var i = 0;
            foreach (var el in arr.EnumerateArray())
            {
                vector[i++] = el.GetSingle();
            }
            var lowConfidence = doc.RootElement.TryGetProperty("low_confidence", out var lc) && lc.GetBoolean();
            return new SpeakerEmbedding(vector, lowConfidence);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Speaker-id embedding request failed");
            return null;
        }
    }

    public static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        if (normA == 0 || normB == 0) return 0f;
        return (float)(dot / (Math.Sqrt(normA) * Math.Sqrt(normB)));
    }
}
