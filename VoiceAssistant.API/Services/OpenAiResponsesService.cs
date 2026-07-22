using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace VoiceAssistant.API.Services;

public sealed class OpenAiApiException(string message, bool isRetryable) : ModelApiException(message, isRetryable);

public static class PrimaryModelSettings
{
    public const string OpenAi = "openai";
    public const string Gemini = "gemini";

    public static string Provider(IConfiguration configuration) =>
        string.Equals(configuration["PrimaryModel:Provider"], OpenAi, StringComparison.OrdinalIgnoreCase)
            ? OpenAi
            : Gemini;
}

public sealed record OpenAiFunctionCall(string Name, string Arguments, string CallId);

public sealed record OpenAiResponse(
    IReadOnlyList<JsonElement> Output,
    IReadOnlyList<OpenAiFunctionCall> FunctionCalls,
    string? Text);

// Minimal Responses API transport. Keeping it on HttpClient rather than an SDK
// makes the provider boundary explicit and keeps Gemini available as a simple
// configuration rollback, with no SDK-specific conversation state in storage.
public sealed class OpenAiResponsesService
{
    private const string Endpoint = "https://api.openai.com/v1/responses";
    private const string DefaultModel = "gpt-5.4";

    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenAiResponsesService> _logger;

    public OpenAiResponsesService(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<OpenAiResponsesService> logger)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string Model => _configuration["OpenAI:Model"] ?? DefaultModel;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_configuration["OpenAI:ApiKey"]);

    public async IAsyncEnumerable<string> StreamResponseAsync(
        string systemPrompt,
        IReadOnlyList<GeminiMessage> messages,
        int maxTokens,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = new
        {
            model = Model,
            instructions = systemPrompt,
            input = BuildInput(messages),
            max_output_tokens = maxTokens,
            stream = true,
            store = false,
            reasoning = new { effort = "medium" },
            text = new { verbosity = "medium" }
        };

        using var response = await SendAsync(payload, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            var data = line[6..].Trim();
            if (string.IsNullOrEmpty(data) || data == "[DONE]")
                continue;

            var delta = ParseStreamDelta(data);
            if (delta is not null)
                yield return delta;
        }
    }

    public async Task<OpenAiResponse> CreateResponseAsync(
        string instructions,
        IReadOnlyList<JsonElement> input,
        IReadOnlyList<JsonElement> tools,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            model = Model,
            instructions,
            input,
            tools,
            max_output_tokens = maxTokens,
            parallel_tool_calls = true,
            store = false,
            reasoning = new { effort = "medium" },
            text = new { verbosity = "medium" }
        };

        using var response = await SendAsync(payload, HttpCompletionOption.ResponseContentRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var output = root.TryGetProperty("output", out var outputElement) && outputElement.ValueKind == JsonValueKind.Array
                ? outputElement.EnumerateArray().Select(item => item.Clone()).ToArray()
                : [];
            var calls = output
                .Where(item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call")
                .Select(item => new OpenAiFunctionCall(
                    item.GetProperty("name").GetString() ?? "",
                    item.GetProperty("arguments").GetString() ?? "{}",
                    item.GetProperty("call_id").GetString() ?? ""))
                .Where(call => !string.IsNullOrWhiteSpace(call.Name) && !string.IsNullOrWhiteSpace(call.CallId))
                .ToArray();
            var text = root.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String
                ? outputText.GetString()
                : ExtractText(output);
            return new OpenAiResponse(output, calls, text);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "OpenAI Responses payload has an unexpected shape");
            throw new OpenAiApiException("Некорректный ответ API OpenAI.", false);
        }
    }

    internal static IReadOnlyList<JsonElement> BuildInput(IReadOnlyList<GeminiMessage> messages)
    {
        var input = new List<JsonElement>();
        foreach (var message in messages.Where(message => message.Parts.Any(part => part.Data is not null || !string.IsNullOrWhiteSpace(part.Text))))
        {
            var role = message.Role.Equals("user", StringComparison.OrdinalIgnoreCase) ? "user" : "assistant";
            if (role == "assistant")
            {
                input.Add(JsonSerializer.SerializeToElement(new { role, content = message.Content }));
                continue;
            }

            var content = new List<object>();
            foreach (var part in message.Parts)
            {
                if (!string.IsNullOrWhiteSpace(part.Text))
                {
                    content.Add(new { type = "input_text", text = part.Text });
                    continue;
                }
                if (part.Data is null || !ImageContentInspector.TryNormalizeAttachmentMimeType(part.MimeType, out var mimeType))
                    continue;

                var dataUrl = $"data:{mimeType};base64,{Convert.ToBase64String(part.Data)}";
                content.Add(ImageContentInspector.IsImageMimeType(mimeType)
                    ? new { type = "input_image", image_url = dataUrl }
                    : new { type = "input_file", filename = FileNameForMimeType(mimeType), file_data = dataUrl });
            }
            if (content.Count > 0)
                input.Add(JsonSerializer.SerializeToElement(new { role, content }));
        }
        return input;
    }

    private async Task<HttpResponseMessage> SendAsync(object payload, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        var key = _configuration["OpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(key))
            throw new OpenAiApiException("Ошибка: отсутствует API-ключ OpenAI.", false);

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(120);
        try
        {
            var response = await http.SendAsync(request, completion, cancellationToken);
            if (response.IsSuccessStatusCode)
                return response;

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("OpenAI Responses API failed with status {Status}", response.StatusCode);
            response.Dispose();
            throw new OpenAiApiException($"Ошибка API OpenAI: {(int)response.StatusCode}", IsRetryableStatusCode((int)response.StatusCode));
        }
        catch (OperationCanceledException) { throw; }
        catch (OpenAiApiException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to OpenAI Responses API");
            throw new OpenAiApiException("Ошибка соединения с сервером ИИ.", true);
        }
    }

    private static bool IsRetryableStatusCode(int statusCode) => statusCode is 408 or 409 or 429 || statusCode >= 500;

    private string? ParseStreamDelta(string data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (root.TryGetProperty("type", out var type) &&
                type.GetString() == "response.output_text.delta" &&
                root.TryGetProperty("delta", out var delta) &&
                delta.ValueKind == JsonValueKind.String)
            {
                return delta.GetString();
            }
            if (root.TryGetProperty("type", out type) && type.GetString() == "error")
                throw CreateStreamException(root);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Ignoring malformed OpenAI Responses SSE event");
        }

        return null;
    }

    private static string? ExtractText(IEnumerable<JsonElement> output)
    {
        var text = new StringBuilder();
        foreach (var item in output)
        {
            if (!item.TryGetProperty("type", out var type) || type.GetString() != "message" ||
                !item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("type", out var partType) && partType.GetString() == "output_text" &&
                    part.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String)
                    text.Append(value.GetString());
            }
        }
        return text.Length == 0 ? null : text.ToString();
    }

    private static OpenAiApiException CreateStreamException(JsonElement root)
    {
        var message = root.TryGetProperty("message", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : "Ошибка API OpenAI.";
        return new OpenAiApiException(message ?? "Ошибка API OpenAI.", true);
    }

    private static string FileNameForMimeType(string mimeType) => mimeType switch
    {
        "application/pdf" => "attachment.pdf",
        "text/plain" => "attachment.txt",
        "text/csv" => "attachment.csv",
        "application/json" => "attachment.json",
        _ => "attachment.bin"
    };
}

public interface IPrimaryConversationService
{
    string Provider { get; }
    IAsyncEnumerable<string> StreamResponseAsync(string systemPrompt, List<GeminiMessage> messages, int maxTokens, string? geminiApiKey, bool enableGrounding, CancellationToken cancellationToken);
}

public sealed class PrimaryConversationService : IPrimaryConversationService
{
    private readonly IConfiguration _configuration;
    private readonly GeminiService _gemini;
    private readonly OpenAiResponsesService _openAi;

    public PrimaryConversationService(IConfiguration configuration, GeminiService gemini, OpenAiResponsesService openAi)
    {
        _configuration = configuration;
        _gemini = gemini;
        _openAi = openAi;
    }

    public string Provider => PrimaryModelSettings.Provider(_configuration);

    public IAsyncEnumerable<string> StreamResponseAsync(
        string systemPrompt,
        List<GeminiMessage> messages,
        int maxTokens,
        string? geminiApiKey,
        bool enableGrounding,
        CancellationToken cancellationToken) =>
        Provider == PrimaryModelSettings.OpenAi
            ? _openAi.StreamResponseAsync(systemPrompt, messages, maxTokens, cancellationToken)
            : _gemini.StreamResponseAsync(systemPrompt, messages, model: "gemini-3.5-flash", maxTokens: maxTokens,
                apiKey: geminiApiKey, enableGrounding: enableGrounding, cancellationToken: cancellationToken);
}
