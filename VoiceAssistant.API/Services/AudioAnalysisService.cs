using System.Text.Json;
using System.Text.RegularExpressions;

namespace VoiceAssistant.API.Services;

public class AudioAnalysisService
{
    private readonly string _apiKey;
    private readonly ILogger<AudioAnalysisService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private const string Model = "gemini-2.5-flash";

    // On quiet/unclear audio, Gemini occasionally echoes fragments of its OWN
    // instruction prompt back as if it were "the transcription" instead of
    // following the "return empty on silence" instruction — a real,
    // confirmed-in-production failure mode (seen twice: once via
    // TranscribeAsync, once via CheckUtteranceCompletionAsync, each time the
    // leaked text matched a prompt phrase verbatim and then got spoken back
    // to the user or round-tripped through the main chat model). These
    // phrases are meta-instructions about transcription/JSON formatting that
    // a real person would essentially never say out loud to a voice
    // assistant, so matching on them is a safe, low-false-positive guard.
    // Deliberately one marker per DISTINCT sentence of each prompt (not just
    // the opening line) — independent review of this fix found the initial
    // set only covered the exact two sentences that had already leaked in
    // production, leaving the rest of each prompt (especially
    // TranscribeAsync's tail, which has no other fallback gate the way
    // CheckUtteranceCompletionAsync's JSON-brace check does) free to leak
    // undetected if Gemini echoes a different fragment next time. Not
    // exhaustive down to every sentence — that's a losing long-term game
    // against silent drift whenever the prompt text is next edited; the
    // real fix for that is Gemini's responseSchema/systemInstruction
    // (tracked as a follow-up, not done here). This covers what a second
    // review round flagged as the highest-value remaining gaps.
    private static readonly string[] PromptLeakMarkers =
    [
        "аудиозапись речи пользователя",
        "разговаривающего с голосовым ассистентом",
        "транскрипцию того, что", // shared stem — matches both prompts' "make an accurate transcription of what [was said/the user said]" sentence
        "СТРОГО в формате JSON",
        "текстом транскрипции",
        "слово-паразит в конце вроде", // CheckUtteranceCompletionAsync's longest, most substantive sentence — the one review flagged as most likely to be what Gemini actually latches onto during an echo
        "не придумывай правдоподобный текст", // added when the anti-hallucination instruction (see temperature comment below) was added to both prompts — independent review of that same change caught this file's own warning above going unheeded
    ];

    private static bool LooksLikePromptLeak(string? text) =>
        !string.IsNullOrEmpty(text) && PromptLeakMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));

    // Gemini is being used here as an ASR engine, rather than a conventional
    // speech recognizer. On an occasional long or unclear recording it can
    // get stuck repeating the last phrase it inferred ("угу, угу, ..." or a
    // full sentence hundreds of times). That output must never become a chat
    // message: it is neither useful user intent nor safe model context.
    //
    // This deliberately detects only a substantial *consecutive* loop. A
    // person can naturally repeat a word or correct themselves; four repeats
    // totalling at least twelve words and occupying 40% of the transcript is
    // far beyond that and matches the production failure mode without trying
    // to rewrite ordinary speech.
    private static readonly Regex WordRegex = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    public static string? RemovePathologicalRepetition(string? transcription)
    {
        if (string.IsNullOrWhiteSpace(transcription)) return transcription;

        var text = transcription.Trim();
        var words = WordRegex.Matches(text);
        const int minimumRepetitions = 4;
        const int minimumRepeatedWords = 12;
        const int maximumPhraseWords = 12;

        for (var start = 0; start < words.Count; start++)
        {
            var maximumPhraseLength = Math.Min(maximumPhraseWords, (words.Count - start) / minimumRepetitions);
            for (var phraseLength = 1; phraseLength <= maximumPhraseLength; phraseLength++)
            {
                var repetitions = 1;
                while (start + (repetitions + 1) * phraseLength <= words.Count &&
                       WordsMatch(words, start, start + repetitions * phraseLength, phraseLength))
                {
                    repetitions++;
                }

                var repeatedWords = repetitions * phraseLength;
                if (repetitions < minimumRepetitions ||
                    repeatedWords < minimumRepeatedWords ||
                    repeatedWords * 100 < words.Count * 40)
                {
                    continue;
                }

                // Keep only speech before the detected loop. A suffix after
                // a model loop is no more trustworthy than the loop itself.
                var prefix = start == 0 ? "" : text[..words[start].Index].TrimEnd(' ', ',', ';', ':', '-');
                return WordRegex.Matches(prefix).Count >= 3 ? prefix : null;
            }
        }

        return text;
    }

    private static bool WordsMatch(MatchCollection words, int firstStart, int secondStart, int length)
    {
        for (var index = 0; index < length; index++)
        {
            if (!string.Equals(words[firstStart + index].Value, words[secondStart + index].Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private string? FilterPathologicalRepetition(string? transcription, string source)
    {
        var filtered = RemovePathologicalRepetition(transcription);
        if (!string.Equals(transcription?.Trim(), filtered, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Discarded pathological repeated ASR output from {Source}. OriginalWordCount: {OriginalWordCount}, RetainedWordCount: {RetainedWordCount}",
                source,
                WordRegex.Matches(transcription ?? "").Count,
                WordRegex.Matches(filtered ?? "").Count);
        }

        return filtered;
    }

    // Best-effort salvage when Gemini's response got cut off mid-generation
    // (hit maxOutputTokens before the JSON object could close) — rather than
    // discarding a real, if incomplete, transcription, pull out whatever text
    // WAS written to the "transcription" field. Deliberately narrow: this is
    // NOT a general JSON-repair routine, just enough hand-rolled scanning to
    // find the field's opening quote and walk to either its closing quote
    // (present) or the end of the string (truncated mid-value, the expected
    // case here) while respecting backslash-escapes so an escaped quote
    // inside the transcription doesn't end the scan early.
    private static string? TryExtractPartialTranscription(string content)
    {
        const string marker = "\"transcription\"";
        var markerIndex = content.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0) return null;

        var colonIndex = content.IndexOf(':', markerIndex + marker.Length);
        if (colonIndex < 0) return null;

        var quoteStart = content.IndexOf('"', colonIndex + 1);
        if (quoteStart < 0) return null;

        var i = quoteStart + 1;
        while (i < content.Length && content[i] != '"')
        {
            if (content[i] == '\\') i++; // skip the escaped character too
            i++;
        }

        var raw = content[(quoteStart + 1)..Math.Min(i, content.Length)];
        return UnescapeJsonString(raw).Trim();
    }

    // Single-pass unescape — chained global .Replace() calls (the original
    // version of this) are unsound for JSON: after a \\ → \ replace runs,
    // a genuine \n newline-escape becomes indistinguishable from a literal
    // backslash-then-letter-n that happened to appear in the text (\\n in
    // the raw JSON), so a later \n → " " replace would wrongly eat the
    // literal "n" too. Found by independent review. Scanning once and
    // consuming each escape pair together avoids the whole class of
    // ordering bugs.
    private static string UnescapeJsonString(string raw)
    {
        var sb = new System.Text.StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '\\' && i + 1 < raw.Length)
            {
                i++;
                sb.Append(raw[i] switch
                {
                    '"' => '"',
                    '\\' => '\\',
                    'n' => ' ',
                    'r' => ' ',
                    't' => ' ',
                    var other => other, // unrecognized escape — keep the character, drop the backslash
                });
            }
            else
            {
                sb.Append(raw[i]);
            }
        }
        return sb.ToString();
    }

    public AudioAnalysisService(IConfiguration config, ILogger<AudioAnalysisService> logger, IHttpClientFactory httpClientFactory)
    {
        _apiKey = config["Gemini:ApiKey"]!;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<string?> TranscribeAsync(string audioPath, string? apiKey = null)
    {
        var key = string.IsNullOrWhiteSpace(apiKey) ? _apiKey : apiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            _logger.LogWarning("Gemini API key is missing. Cannot transcribe audio.");
            return null;
        }

        var audioBytes = await File.ReadAllBytesAsync(audioPath);
        var base64Audio = Convert.ToBase64String(audioBytes);
        var ext = Path.GetExtension(audioPath).TrimStart('.'); // wav, mp3, ogg
        var mimeType = ext switch
        {
            "wav" => "audio/wav",
            "mp3" => "audio/mp3",
            "ogg" => "audio/ogg",
            "flac" => "audio/flac",
            _ => "audio/wav"
        };

        using var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(45);

        var payload = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new
                        {
                            inline_data = new { mime_type = mimeType, data = base64Audio }
                        },
                        new
                        {
                            text = """
                                Это аудиозапись речи пользователя, говорящего на русском или украинском языке.
                                Сделай точную транскрипцию того, что пользователь сказал в записи. Убери посторонние вздохи, шумы или щелчки, оставив чистый текст.
                                Ответь ТОЛЬКО текстом транскрипции, без кавычек и пояснений. Если запись пустая, тихая или не содержит речи, верни пустую строку.
                                Если не уверен, что именно было сказано — НЕ придумывай правдоподобный текст. Лучше вернуть пустую строку, чем угадать неверно.
                                """
                        }
                    }
                }
            },
            generationConfig = new
            {
                // Was 256 — confirmed too small in production for anything
                // beyond a short phrase: on a long, pause-free monologue the
                // sibling CheckUtteranceCompletionAsync call (same audio
                // segment, near-identical budget) truncated Gemini's JSON
                // response mid-transcription. This method returns plain text
                // rather than JSON, so the same failure here wouldn't throw
                // a parse error — it would just silently hand back a
                // truncated transcript with no error at all, an even harder
                // failure mode to notice than the JSON one. Raised well
                // past any realistic single-segment transcript length.
                maxOutputTokens = 2048,
                thinkingConfig = new { thinkingBudget = 0 },
                // Faithful transcription needs the model to reproduce what it
                // actually heard, not creatively complete an ambiguous/quiet
                // clip into a plausible-sounding sentence — the default
                // temperature is tuned for conversational generation, not
                // extraction. Confirmed in production: on unclear audio,
                // Gemini fabricated a coherent-but-entirely-invented request
                // ("Алло, Сбер, включи музыку...") that the user never said —
                // not a prompt-leak (a different, already-handled failure
                // mode), a genuine ASR-style hallucination. Low temperature
                // reduces (does not eliminate) this: it makes the model favor
                // its single most-probable read of the audio consistently,
                // rather than sampling toward a fluent alternative when
                // uncertain. Known tradeoff (raised by independent review,
                // worth recording rather than glossing over): temperature=0
                // is greedy argmax decoding — if fabrication IS the model's
                // top-probability read for some recurring noise pattern (a
                // washing machine, a TV), 0.0 makes that exact wrong output
                // deterministic and repeatable instead of an occasional
                // sampling-driven miss. The upside of that same determinism:
                // any such case becomes reproducible from the saved audio,
                // unlike a one-off hallucination under nonzero temperature.
                temperature = 0.0
            }
        };

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Model}:generateContent?key={key}";

        try
        {
            var response = await http.PostAsync(url,
                new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json"));

            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Gemini API error {Status}: {Body}", response.StatusCode, body);
                return null;
            }

            var doc = JsonDocument.Parse(body);
            var parts = doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts");
            var content = string.Join("", parts.EnumerateArray()
                .Where(p => p.TryGetProperty("text", out _))
                .Select(p => p.GetProperty("text").GetString() ?? ""));

            var transcription = content.Trim().Trim('"');
            if (LooksLikePromptLeak(transcription))
            {
                _logger.LogWarning("Gemini echoed its own prompt instead of transcribing — treating as no speech. Raw: {Content}", content);
                return null;
            }

            return FilterPathologicalRepetition(transcription, "direct transcription");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gemini transcription failed");
            return null;
        }
    }

    // Transcribes a (possibly not-yet-finished) recording snapshot AND judges whether
    // the speaker sounds done with their thought (ready for a response) or is likely
    // still talking/pausing to think. Used to give a real conversational partner's
    // patience instead of cutting people off on a fixed short silence timeout.
    //
    // Known limitation: this check never sees companion-system.txt (it's a separate,
    // lightweight call) so it can't extend extra patience for emotionally-loaded pauses
    // the way the main persona prompt might imply. Its patience is topic-blind and comes
    // entirely from the fixed timing in yolo.js (MAX_SILENCE_CEILING / RECHECK_INTERVAL),
    // applied uniformly regardless of what's being said.
    public async Task<UtteranceCheckResult?> CheckUtteranceCompletionAsync(string audioPath, string? apiKey = null)
    {
        var key = string.IsNullOrWhiteSpace(apiKey) ? _apiKey : apiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            _logger.LogWarning("Gemini API key is missing. Cannot check utterance completion.");
            return null;
        }

        var audioBytes = await File.ReadAllBytesAsync(audioPath);
        var base64Audio = Convert.ToBase64String(audioBytes);
        var ext = Path.GetExtension(audioPath).TrimStart('.');
        var mimeType = ext switch
        {
            "wav" => "audio/wav",
            "mp3" => "audio/mp3",
            "ogg" => "audio/ogg",
            "flac" => "audio/flac",
            _ => "audio/wav"
        };

        using var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(45);

        var payload = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { inline_data = new { mime_type = mimeType, data = base64Audio } },
                        new
                        {
                            text = """
                                Это аудиозапись человека, разговаривающего с голосовым ассистентом. Он мог уже
                                закончить свою мысль и ждать ответа, а мог просто взять паузу, чтобы подумать,
                                и собирается продолжить говорить.
                                Сделай точную транскрипцию того, что было сказано (без вздохов/шумов).
                                Затем оцени: похоже ли, что человек закончил (законченный вопрос, просьба, явная
                                пауза для ответа) — или он, вероятно, продолжит (оборванная фраза, слово-паразит
                                в конце вроде "ну", "вот", "это", "так", явно неполная мысль).
                                Ответь СТРОГО в формате JSON без markdown:
                                {"transcription": "текст", "complete": true или false}
                                Если записи не слышно/тишина, верни {"transcription": "", "complete": false}.
                                Если не уверен, что именно было сказано — НЕ придумывай правдоподобный текст.
                                Лучше вернуть пустую transcription, чем угадать неверно.
                                """
                        }
                    }
                }
            },
            generationConfig = new
            {
                // Root-caused live from a real device test: user reported a
                // long, detailed monologue (a full workout description)
                // vanishing down to a short leftover fragment. Server logs
                // showed why — "No JSON found in completion-check response"
                // with a Raw payload containing a perfectly real, in-progress
                // transcription of their actual speech, cut off mid-word
                // with no closing quote or brace. maxOutputTokens=300 was
                // nowhere near enough once the transcription value itself
                // (not just the small JSON wrapper around it) got long — a
                // continuous monologue with no pause over
                // CHECK_SILENCE_THRESHOLD_MS never triggers an earlier
                // check, so the ENTIRE segment (96s of speech in the
                // reported incident) rides on one call fitting in budget.
                // See TryExtractPartialTranscription below for the
                // complementary defense-in-depth for whatever still
                // exceeds even this.
                maxOutputTokens = 2048,
                thinkingConfig = new { thinkingBudget = 0 },
                // See TranscribeAsync's identical setting for why — same
                // fabrication risk applies here, confirmed in production.
                temperature = 0.0
            }
        };

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Model}:generateContent?key={key}";

        try
        {
            var response = await http.PostAsync(url,
                new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json"));

            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Gemini API error {Status}: {Body}", response.StatusCode, body);
                return null;
            }

            var doc = JsonDocument.Parse(body);
            var parts = doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts");
            var content = string.Join("", parts.EnumerateArray()
                .Where(p => p.TryGetProperty("text", out _))
                .Select(p => p.GetProperty("text").GetString() ?? ""));

            var clean = content.Trim();
            if (clean.StartsWith("```"))
            {
                clean = clean[clean.IndexOf('\n')..];
                var endFence = clean.LastIndexOf("```");
                if (endFence >= 0) clean = clean[..endFence];
                clean = clean.Trim();
            }

            var jsonStart = clean.IndexOf('{');
            var jsonEnd = clean.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd < 0)
            {
                // Response got cut off before the JSON closed — almost
                // certainly maxOutputTokens hit mid-transcription (see the
                // comment on maxOutputTokens above), not silence/no-speech.
                // Salvage whatever transcription text WAS generated rather
                // than discarding a real, if incomplete, utterance —
                // Complete is deliberately false here since the model never
                // reached its own judgment on that, so the caller correctly
                // keeps waiting for more / eventually hits the hard ceiling
                // instead of prematurely treating a cut-off fragment as
                // "done."
                var partial = TryExtractPartialTranscription(clean);
                // Checked against the WHOLE response (clean), not just the
                // narrowly-extracted partial — independent review found a
                // real gap: the prompt's own JSON example
                // ({"transcription": "текст", ...}) means "transcription"
                // can appear before any real leaked instructional text, so
                // IndexOf's first match could grab the placeholder word
                // "текст" — which alone matches none of PromptLeakMarkers —
                // while the REST of clean plainly contains a leak. clean is
                // a superset of partial, so this check is strictly more
                // thorough, not just different.
                if (!string.IsNullOrWhiteSpace(partial) && !LooksLikePromptLeak(clean))
                {
                    _logger.LogWarning(
                        "Completion-check response truncated before JSON closed — salvaged {Length}-char partial transcription. Raw: {Content}",
                        partial.Length, content);
                    return new UtteranceCheckResult
                    {
                        Transcription = FilterPathologicalRepetition(partial, "completion-check partial") ?? "",
                        Complete = false
                    };
                }
                _logger.LogWarning("No JSON found in completion-check response: {Content}", content);
                return null;
            }

            var resultJson = clean[jsonStart..(jsonEnd + 1)];
            var result = JsonSerializer.Deserialize<UtteranceCheckResult>(resultJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (result != null && LooksLikePromptLeak(result.Transcription))
            {
                _logger.LogWarning("Gemini echoed its own prompt instead of transcribing — treating as no speech. Raw: {Content}", content);
                return new UtteranceCheckResult { Transcription = "", Complete = false };
            }

            if (result != null)
            {
                result.Transcription = FilterPathologicalRepetition(result.Transcription, "completion check") ?? "";
                if (string.IsNullOrWhiteSpace(result.Transcription)) result.Complete = false;
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gemini utterance-completion check failed");
            return null;
        }
    }
}

public class UtteranceCheckResult
{
    public string Transcription { get; set; } = "";
    public bool Complete { get; set; }
}
