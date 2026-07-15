using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace VoiceAssistant.API.Services;

public record GeminiPart(string? Text = null, string? MimeType = null, byte[]? Data = null);

// Content remains available for the existing text-only side-call services.
// Parts lets the primary conversation add one inline attachment without
// changing side-call contracts or accidentally feeding private bytes into them.
public record GeminiMessage(string Role, string Content)
{
    public IReadOnlyList<GeminiPart> Parts { get; init; } = [new(Text: Content)];

    public GeminiMessage(string role, IReadOnlyList<GeminiPart> parts)
        : this(role, ExtractText(parts))
    {
        Parts = parts;
    }

    private static string ExtractText(IReadOnlyList<GeminiPart> parts)
    {
        var text = new StringBuilder();
        foreach (var part in parts)
        {
            if (part.Text is not null) text.Append(part.Text);
        }
        return text.ToString();
    }
}

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
    public const string EmbeddingModel = "gemini-embedding-2";
    public const int EmbeddingDimensions = 768;

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

    public async Task<float[]> GenerateEmbeddingAsync(
        string text,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        var key = string.IsNullOrWhiteSpace(apiKey) ? _defaultApiKey : apiKey;
        if (string.IsNullOrWhiteSpace(key))
            throw new GeminiApiException("Ошибка: отсутствует API-ключ Gemini.", isRetryable: false);

        var payload = JsonSerializer.Serialize(new
        {
            model = $"models/{EmbeddingModel}",
            content = new { parts = new[] { new { text } } },
            output_dimensionality = EmbeddingDimensions
        });
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{EmbeddingModel}:embedContent?key={key}";

        using var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Gemini embeddings API");
            throw new GeminiApiException("Ошибка соединения с сервером embeddings.", isRetryable: true);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Gemini embeddings API error {Status}: {Body}", response.StatusCode, body);
                throw new GeminiApiException($"Ошибка API embeddings: {(int)response.StatusCode}",
                    IsRetryableStatusCode((int)response.StatusCode));
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                var values = document.RootElement
                    .GetProperty("embedding")
                    .GetProperty("values")
                    .EnumerateArray()
                    .Select(value => value.GetSingle())
                    .ToArray();

                if (values.Length != EmbeddingDimensions)
                    throw new GeminiApiException($"Gemini вернул embedding размерности {values.Length} вместо {EmbeddingDimensions}.", false);

                return values;
            }
            catch (GeminiApiException)
            {
                throw;
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                _logger.LogError(ex, "Gemini embeddings response has an unexpected shape: {Body}", body);
                throw new GeminiApiException("Некорректный ответ API embeddings.", false);
            }
        }
    }

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
            throw new GeminiApiException("Ошибка: отсутствует API-ключ Gemini.", isRetryable: false);
        }

        // Format request body for Gemini API: roles must be "user" or "model".
        // Text-only callers retain the exact same JSON shape as before.
        var contents = messages
            .Where(message => message.Parts.Any(part =>
                part.Data is not null || !string.IsNullOrWhiteSpace(part.Text)))
            .Select(m => new
            {
                role = m.Role.ToLower() == "user" ? "user" : "model",
                parts = SerializeParts(m.Parts)
            })
            .ToArray();
        if (contents.Length == 0)
            throw new GeminiApiException("Невозможно отправить пустой контекст в Gemini.", isRetryable: false);

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
        http.Timeout = TimeSpan.FromSeconds(120);

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
                            // Search-grounded responses may begin with a technical or
                            // thought part, which has no `text` field. Treat the SSE
                            // shape as additive and emit only visible text parts.
                            var textParts = new StringBuilder();
                            foreach (var part in parts.EnumerateArray())
                            {
                                if (part.ValueKind != JsonValueKind.Object ||
                                    (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True) ||
                                    !part.TryGetProperty("text", out var text) ||
                                    text.ValueKind != JsonValueKind.String)
                                {
                                    continue;
                                }

                                textParts.Append(text.GetString());
                            }

                            chunkText = textParts.Length > 0 ? textParts.ToString() : null;
                        }

                        // Diagnostic: confirm whether Google Search grounding actually fired for
                        // this turn — real web search execution is much slower than plain generation.
                        if (firstCandidate.TryGetProperty("groundingMetadata", out var grounding) &&
                            grounding.TryGetProperty("webSearchQueries", out var queries) &&
                            queries.ValueKind == JsonValueKind.Array)
                        {
                            // Search terms are user content. Retain only a
                            // content-free operational signal in application logs.
                            _logger.LogInformation("Gemini used Google Search grounding ({QueryCount} query/queries)", queries.GetArrayLength());
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

    private static object[] SerializeParts(IReadOnlyList<GeminiPart> parts)
    {
        var nonEmptyParts = parts
            .Where(part => part.Data is not null || !string.IsNullOrWhiteSpace(part.Text))
            .ToArray();
        if (nonEmptyParts.Length == 0) throw new ArgumentException("A Gemini message must contain at least one part.", nameof(parts));

        var serialized = new object[nonEmptyParts.Length];
        for (var index = 0; index < nonEmptyParts.Length; index++)
        {
            var part = nonEmptyParts[index];
            if (part.Data is not null)
            {
                if (part.Data.Length == 0 || part.Data.Length > ImageContentInspector.MaxAttachmentSize)
                    throw new ArgumentException("Attachment content has an invalid size.", nameof(parts));
                if (!ImageContentInspector.TryNormalizeAttachmentMimeType(part.MimeType, out var mimeType))
                    throw new ArgumentException("Attachment content has an invalid MIME type.", nameof(parts));
                serialized[index] = new { inline_data = new { mime_type = mimeType, data = Convert.ToBase64String(part.Data) } };
                continue;
            }

            if (string.IsNullOrWhiteSpace(part.Text))
                throw new ArgumentException("Text content cannot be empty.", nameof(parts));
            serialized[index] = new { text = part.Text };
        }
        return serialized;
    }
}
