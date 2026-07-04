using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace VoiceAssistant.API.Services;

public class OpenAiChatService
{
    private readonly string _defaultApiKey;
    private readonly ILogger<OpenAiChatService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public OpenAiChatService(IConfiguration config, ILogger<OpenAiChatService> logger, IHttpClientFactory httpClientFactory)
    {
        _defaultApiKey = config["OpenAI:ApiKey"] ?? "";
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public async IAsyncEnumerable<string> StreamResponseAsync(
        string systemPrompt,
        List<GeminiMessage> messages,
        string model = "gpt-5.5",
        string reasoningEffort = "medium",
        int maxTokens = 2048,
        string? apiKey = null)
    {
        var key = string.IsNullOrWhiteSpace(apiKey) ? _defaultApiKey : apiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            _logger.LogError("OpenAI API key is missing.");
            yield return "Ошибка: отсутствует API-ключ OpenAI.";
            yield break;
        }

        var chatMessages = new List<object> { new { role = "system", content = systemPrompt } };
        chatMessages.AddRange(messages.Select(m => (object)new
        {
            role = m.Role.ToLower() == "user" ? "user" : "assistant",
            content = m.Content
        }));

        var payload = new
        {
            model,
            messages = chatMessages,
            stream = true,
            reasoning_effort = reasoningEffort,
            max_completion_tokens = maxTokens
        };

        var json = JsonSerializer.Serialize(payload);

        using var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(60);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage? response = null;
        string? errorMessage = null;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync();
                _logger.LogError("OpenAI Chat API error {Status}: {Body}", response.StatusCode, errBody);
                errorMessage = $"Ошибка API OpenAI: {(int)response.StatusCode}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to OpenAI Chat API");
            errorMessage = "Ошибка соединения с сервером ИИ.";
        }

        if (errorMessage != null)
        {
            yield return errorMessage;
            yield break;
        }

        if (response == null) yield break;

        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (!line.StartsWith("data: ")) continue;
            var data = line[6..].Trim();
            if (string.IsNullOrEmpty(data)) continue;
            if (data == "[DONE]") yield break;

            string? chunkText = null;
            try
            {
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                    choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                {
                    var delta = choices[0].GetProperty("delta");
                    if (delta.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                    {
                        chunkText = contentEl.GetString();
                    }
                }
            }
            catch (JsonException)
            {
                // Ignore parsing errors for incomplete JSON or keep-alive lines
            }

            if (!string.IsNullOrEmpty(chunkText))
            {
                yield return chunkText;
            }
        }
    }
}
