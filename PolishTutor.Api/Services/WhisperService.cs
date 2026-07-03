using System.Net.Http.Headers;
using System.Text.Json;

namespace PolishTutor.Api.Services;

public class WhisperService
{
    private readonly string _defaultApiKey;

    public WhisperService(IConfiguration config)
    {
        _defaultApiKey = config["OpenAI:ApiKey"]!;
    }

    public async Task<string> TranscribeAsync(string filePath, string? apiKey = null)
    {
        var key = string.IsNullOrWhiteSpace(apiKey) ? _defaultApiKey : apiKey;
        var fileBytes = await File.ReadAllBytesAsync(filePath);

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var form = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/webm");
            form.Add(fileContent, "file", Path.GetFileName(filePath));
            form.Add(new StringContent("gpt-4o-mini-transcribe"), "model");
            form.Add(new StringContent("pl"), "language");
            form.Add(new StringContent("Uczeń mówi po polsku. Transkrybuj tylko po polsku."), "prompt");

            var response = await http.PostAsync(
                "https://api.openai.com/v1/audio/transcriptions", form);

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                await Task.Delay(2000);
                continue;
            }
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<JsonElement>(json);
            return result.GetProperty("text").GetString() ?? "";
        }

        throw new Exception("Transcription API rate limited after retries");
    }
}
