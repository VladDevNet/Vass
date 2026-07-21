using System.Text;
using System.Text.Json;

namespace VoiceAssistant.API.Services;

// A narrow, feature-gated bridge from the original phone recording to the
// primary Gemini model. It asks the model for a native function call rather
// than a JSON-shaped text response, so the transcript has a typed boundary
// before it enters the existing agent/tool pipeline.
public sealed record AudioCoreTranscriptionResult(
    string Transcription,
    string? Preamble,
    bool RequiresTool,
    bool ProviderAvailable)
{
    public static AudioCoreTranscriptionResult Unavailable() => new("", null, true, false);
}

public sealed class AudioCoreTranscriptionService
{
    private const string Model = "gemini-3.5-flash";
    private const string CaptureFunctionName = "capture_user_utterance";
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AudioCoreTranscriptionService> _logger;

    public AudioCoreTranscriptionService(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<AudioCoreTranscriptionService> logger)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<AudioCoreTranscriptionResult> TranscribeAsync(
        string audioPath,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        var key = string.IsNullOrWhiteSpace(apiKey) ? _configuration["Gemini:ApiKey"] : apiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            _logger.LogWarning("AudioCore transcription skipped because no Gemini API key is configured");
            return AudioCoreTranscriptionResult.Unavailable();
        }

        try
        {
            var audioBytes = await File.ReadAllBytesAsync(audioPath, cancellationToken);
            if (audioBytes.Length == 0) return new AudioCoreTranscriptionResult("", null, true, true);

            var payload = new
            {
                contents = new[]
                {
                    new
                    {
                        role = "user",
                        parts = new object[]
                        {
                            new
                            {
                                inline_data = new
                                {
                                    mime_type = DetectAudioMimeType(audioBytes),
                                    data = Convert.ToBase64String(audioBytes)
                                }
                            },
                            new
                            {
                                text = "Сделай точную транскрипцию текущей голосовой реплики пользователя."
                            }
                        }
                    }
                },
                systemInstruction = new
                {
                    parts = new[]
                    {
                        new
                        {
                            text = """
                                Ты получаешь одну аудиозапись речи пользователя на русском или украинском языке.
                                Ровно один раз вызови capture_user_utterance и передай в transcript точную
                                транскрипцию сказанного. Убери только шумы, вздохи и щелчки. Не дополняй
                                неразборчивую речь догадками. Если речи нет, передай пустую строку.
                                В preamble передай ровно одну короткую нейтральную реплику ожидания: 2-7
                                слов, на языке пользователя и с завершающей пунктуацией. Это не начало
                                ответа и не план действий. Она может лишь мягко отразить тему реплики,
                                например «Про рецепт — секунду.» или «Понял, минутку.», но не должна
                                делать выводов, отвечать по существу, обещать действие или результат.
                                Нельзя использовать «сделаю», «найду», «открою», «запишу», «проверю» и
                                другие формулировки, предвосхищающие дальнейший ответ. Для пустой записи
                                передай пустую строку.
                                В requiresTool передай true, если пользователь явно просит Vass выполнить
                                действие через память, напоминание, снимок экрана, overlay, YouTube,
                                локальную библиотеку, книгу или отчет, возможности приложения, поиск
                                свежих новостей, текущей погоды, цен, расписания или явный поиск
                                в интернете. Если
                                не уверен, передай true. Передавай false только для обычного разговора,
                                эмоциональной поддержки или вопроса, на который достаточно обычного
                                текстового ответа без актуальной интернет-проверки.
                                Не отвечай пользователю и не пиши пояснений: эта функция является твоим
                                единственным результатом.
                                """
                        }
                    }
                },
                tools = new object[]
                {
                    new
                    {
                        functionDeclarations = new object[]
                        {
                            new
                            {
                                name = CaptureFunctionName,
                                description = "Передает точную расшифровку одной реплики пользователя.",
                                parameters = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        transcript = new
                                        {
                                            type = "string",
                                            description = "Точная транскрипция без пояснений; пустая строка для тишины."
                                        },
                                        preamble = new
                                        {
                                            type = "string",
                                            description = "Короткая безопасная фраза для немедленного голосового ответа; пустая строка для тишины."
                                        },
                                        requiresTool = new
                                        {
                                            type = "boolean",
                                            description = "true, когда для запроса нужен инструмент Vass или есть сомнение."
                                        }
                                    },
                                    required = new[] { "transcript", "preamble", "requiresTool" }
                                }
                            }
                        }
                    }
                },
                toolConfig = new { functionCallingConfig = new { mode = "AUTO" } },
                generationConfig = new
                {
                    maxOutputTokens = 1024,
                    thinkingConfig = new { thinkingBudget = 0 },
                    temperature = 0.0
                }
            };

            using var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(45);
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Model}:generateContent?key={key}";
            var response = await http.PostAsync(
                url,
                new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
                cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("AudioCore transcription failed with Gemini status {Status}", response.StatusCode);
                return AudioCoreTranscriptionResult.Unavailable();
            }

            var result = ParseResponse(body);
            if (!result.ProviderAvailable)
            {
                _logger.LogWarning("AudioCore transcription response did not contain the expected function call");
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AudioCore transcription failed; caller may use the legacy ASR fallback");
            return AudioCoreTranscriptionResult.Unavailable();
        }
    }

    internal static AudioCoreTranscriptionResult ParseResponse(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("candidates", out var candidates) ||
                candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0 ||
                !candidates[0].TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Object ||
                !content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
            {
                return AudioCoreTranscriptionResult.Unavailable();
            }

            foreach (var part in parts.EnumerateArray())
            {
                if (!part.TryGetProperty("functionCall", out var call) || call.ValueKind != JsonValueKind.Object ||
                    !call.TryGetProperty("name", out var name) ||
                    !string.Equals(name.GetString(), CaptureFunctionName, StringComparison.Ordinal) ||
                    !call.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Object ||
                    !args.TryGetProperty("transcript", out var transcript) || transcript.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var filtered = AudioAnalysisService.RemovePathologicalRepetition(transcript.GetString()?.Trim());
                var preamble = args.TryGetProperty("preamble", out var preambleValue) && preambleValue.ValueKind == JsonValueKind.String
                    ? NormalizePreamble(preambleValue.GetString())
                    : null;
                // A missing/invalid decision must remain conservative: the
                // regular planner still runs rather than skipping an action.
                var requiresTool = !args.TryGetProperty("requiresTool", out var requiresToolValue) ||
                                   requiresToolValue.ValueKind != JsonValueKind.False;
                return new AudioCoreTranscriptionResult(filtered ?? "", preamble, requiresTool, true);
            }
        }
        catch (JsonException)
        {
            // The caller has a proven legacy conversion/ASR fallback for a
            // transient malformed provider response.
        }

        return AudioCoreTranscriptionResult.Unavailable();
    }

    // This is a transport guard, not an attempt to infer user intent. It
    // keeps a malformed provider value from becoming a long or empty spoken
    // interruption before the full answer arrives.
    internal static string? NormalizePreamble(string? value)
    {
        var compact = value?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(compact) || compact.Length > 120) return null;

        var words = compact.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length is >= 2 and <= 7 ? string.Join(' ', words) : null;
    }

    // Android's Expo recorder writes AAC in an MPEG-4/M4A container although
    // the historical upload filename has a .webm suffix. Gemini accepted the
    // real production bytes as audio/aac in the live compatibility check.
    // Detect genuinely older WebM/Ogg/WAV uploads so a rollout can safely
    // coexist with recordings captured by an older app build.
    internal static string DetectAudioMimeType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0x1A && bytes[1] == 0x45 && bytes[2] == 0xDF && bytes[3] == 0xA3)
            return "audio/webm";
        if (bytes.Length >= 4 && bytes[0] == (byte)'O' && bytes[1] == (byte)'g' && bytes[2] == (byte)'g' && bytes[3] == (byte)'S')
            return "audio/ogg";
        if (bytes.Length >= 12 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
            bytes[8] == (byte)'W' && bytes[9] == (byte)'A' && bytes[10] == (byte)'V' && bytes[11] == (byte)'E')
            return "audio/wav";
        if (bytes.Length >= 3 && bytes[0] == (byte)'I' && bytes[1] == (byte)'D' && bytes[2] == (byte)'3')
            return "audio/mp3";

        return "audio/aac";
    }
}
