using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace VoiceAssistant.API.Services;

public record GeminiMessage(string Role, string Content);

// Typed error channel for StreamResponseAsync's infrastructure failures
// (missing key, non-2xx HTTP, connection error) -- previously these were
// yielded as Russian error strings indistinguishable from real model output,
// so callers that persist/parse the result (not just relay it to a client)
// could save an error message as if it were genuine AI content
// (PROJECT-AUDIT-2026-07-10 REL-04). IsRetryable lets a caller decide
// whether surfacing "try again" makes sense (transient/rate-limit) vs. not
// (misconfiguration).
public class GeminiApiException(string message, bool isRetryable) : Exception(message)
{
    public bool IsRetryable { get; } = isRetryable;
}

public class GeminiService
{
    private readonly string _defaultApiKey;
    private readonly ILogger<GeminiService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public GeminiService(IConfiguration config, ILogger<GeminiService> logger, IHttpClientFactory httpClientFactory)
    {
        _defaultApiKey = config["Gemini:ApiKey"] ?? "";
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    // 429 (rate limit) and 5xx are transient -- retrying later has a real
    // chance of succeeding. Everything else (4xx auth/validation errors) is a
    // configuration problem retrying won't fix.
    public static bool IsRetryableStatusCode(int statusCode) => statusCode == 429 || statusCode >= 500;

    public async IAsyncEnumerable<string> StreamResponseAsync(
        string systemPrompt,
        List<GeminiMessage> messages,
        string model = "gemini-3.5-flash",
        int maxTokens = 2048,
        string? apiKey = null,
        bool enableGrounding = true,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var key = string.IsNullOrWhiteSpace(apiKey) ? _defaultApiKey : apiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            _logger.LogError("Gemini API key is missing.");
            throw new GeminiApiException("Отсутствует API-ключ Gemini.", isRetryable: false);
        }

        // Format request body for Gemini API: roles must be "user" or "model"
        var contents = messages.Select(m => new
        {
            role = m.Role.ToLower() == "user" ? "user" : "model",
            parts = new[] { new { text = m.Content } }
        }).ToArray();

        var payload = new
        {
            contents,
            systemInstruction = new
            {
                parts = new[] { new { text = systemPrompt } }
            },
            tools = enableGrounding ? new object[] { new { google_search = new { } } } : Array.Empty<object>(),
            generationConfig = new
            {
                maxOutputTokens = maxTokens,
                thinkingConfig = new { thinkingBudget = 0 }
            }
        };

        var json = JsonSerializer.Serialize(payload);
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:streamGenerateContent?alt=sse&key={key}";

        using var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(60);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync();
                _logger.LogError("Gemini API error {Status}: {Body}", response.StatusCode, errBody);
                throw new GeminiApiException($"Ошибка API Gemini: {(int)response.StatusCode}", IsRetryableStatusCode((int)response.StatusCode));
            }
        }
        catch (OperationCanceledException)
        {
            // Caller (client) disconnected/aborted — propagate as cancellation, not a
            // content chunk, so nothing gets saved as if the model actually said it.
            throw;
        }
        catch (GeminiApiException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Gemini API");
            throw new GeminiApiException("Ошибка соединения с сервером ИИ.", isRetryable: true);
        }

        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            if (line.StartsWith("data: "))
            {
                var data = line.Substring(6).Trim();
                if (string.IsNullOrEmpty(data)) continue;

                string? chunkText = null;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    if (doc.RootElement.TryGetProperty("candidates", out var candidates) &&
                        candidates.ValueKind == JsonValueKind.Array &&
                        candidates.GetArrayLength() > 0)
                    {
                        var firstCandidate = candidates[0];
                        if (firstCandidate.TryGetProperty("content", out var content) &&
                            content.TryGetProperty("parts", out var parts) &&
                            parts.ValueKind == JsonValueKind.Array &&
                            parts.GetArrayLength() > 0)
                        {
                            chunkText = parts[0].GetProperty("text").GetString();
                        }

                        // Diagnostic: confirm whether Google Search grounding actually fired for
                        // this turn — real web search execution is much slower than plain generation.
                        if (firstCandidate.TryGetProperty("groundingMetadata", out var grounding) &&
                            grounding.TryGetProperty("webSearchQueries", out var queries) &&
                            queries.ValueKind == JsonValueKind.Array)
                        {
                            var queryList = string.Join(", ", queries.EnumerateArray().Select(q => q.GetString()));
                            _logger.LogInformation("Gemini used Google Search grounding, queries: [{Queries}]", queryList);
                        }
                    }
                }
                catch (JsonException)
                {
                    // Ignore parsing errors for incomplete JSON or keep-alive lines
                }

                if (chunkText != null)
                {
                    yield return chunkText;
                }
            }
        }
    }
}
