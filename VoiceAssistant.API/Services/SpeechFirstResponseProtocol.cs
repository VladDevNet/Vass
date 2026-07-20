using System.Text;

namespace VoiceAssistant.API.Services;

// The model produces a Russian-phonetic speech form paired with each normal
// user-facing sentence. These markers are internal to the server: they never
// reach a client, chat history, or a TextToSpeech engine.
public enum SpeechFirstResponsePart
{
    Speech,
    Text
}

public sealed record SpeechFirstResponseChunk(SpeechFirstResponsePart Part, string Text);

public sealed class SpeechFirstResponseParser
{
    public const string SpeechStartMarker = "[[VASS_SPEECH]]";
    public const string TextStartMarker = "[[VASS_TEXT]]";

    private enum ParseState
    {
        AwaitingSpeechMarker,
        Speech,
        Text,
        LegacyText
    }

    private readonly StringBuilder _buffer = new();
    private readonly StringBuilder _speech = new();
    private ParseState _state = ParseState.AwaitingSpeechMarker;
    private bool _finished;

    public bool HasSpeech { get; private set; }
    public bool HasText { get; private set; }

    public IReadOnlyList<SpeechFirstResponseChunk> Append(string? value)
    {
        if (_finished || string.IsNullOrEmpty(value)) return [];

        var emitted = new List<SpeechFirstResponseChunk>();
        if (_state == ParseState.LegacyText)
        {
            Emit(emitted, SpeechFirstResponsePart.Text, value);
            return emitted;
        }

        _buffer.Append(value);
        Drain(emitted);
        return emitted;
    }

    public IReadOnlyList<SpeechFirstResponseChunk> Finish()
    {
        if (_finished) return [];
        _finished = true;

        var emitted = new List<SpeechFirstResponseChunk>();
        var buffered = _buffer.ToString();
        _buffer.Clear();

        switch (_state)
        {
            case ParseState.AwaitingSpeechMarker:
                // A truncated marker is not a user-facing reply. Leave the
                // response empty so ChatController's existing retry path can
                // recover instead of saving protocol debris to history.
                if (!SpeechStartMarker.StartsWith(buffered.TrimStart(), StringComparison.Ordinal))
                {
                    Emit(emitted, SpeechFirstResponsePart.Text, buffered);
                }
                break;

            case ParseState.Speech:
                // The parser intentionally keeps a possible partial text
                // marker in the buffer. Do not speak or display it if the
                // provider cut the stream off halfway through that marker.
                if (!TextStartMarker.StartsWith(buffered, StringComparison.Ordinal))
                {
                    EmitSpeech(emitted, buffered);
                }
                if (_speech.Length > 0)
                {
                    // Preserve a usable visible answer even when a provider
                    // emits the speech half but drops the display half.
                    Emit(emitted, SpeechFirstResponsePart.Text, _speech.ToString());
                }
                break;

            case ParseState.Text:
            case ParseState.LegacyText:
                Emit(emitted, SpeechFirstResponsePart.Text, buffered);
                break;
        }

        return emitted;
    }

    public static string AddInstructions(string systemPrompt) => $$"""
        {{systemPrompt}}

        ## Внутренний формат голосового ответа
        Этот формат нужен только серверу, пользователь его не увидит. Обычный ответ
        состоит из ПОСЛЕДОВАТЕЛЬНЫХ ПАР, по одной паре на каждое законченное смысловое
        предложение. Начинай ответ ровно с {{SpeechStartMarker}} без приветствия,
        пробелов, Markdown или кодового блока. Затем пиши:

        {{SpeechStartMarker}} фонетическая русская версия первого предложения
        {{TextStartMarker}} обычная видимая версия ТОГО ЖЕ первого предложения
        {{SpeechStartMarker}} фонетическая русская версия второго предложения
        {{TextStartMarker}} обычная видимая версия ТОГО ЖЕ второго предложения

        И так до самого конца ответа. Не пиши сначала короткое устное резюме, а затем
        длинный чат-ответ: голосовая часть каждой пары обязана передавать ВСЕ факты,
        оговорки, выводы и вопрос соответствующего видимого предложения в том же
        порядке. Единственные допустимые различия: русская фонетическая запись слов не
        на кириллице и удаление Markdown, URL или технических названий, которые нельзя
        естественно произнести. Не добавляй новых фактов, обещаний или действий.

        В части сразу после каждого {{SpeechStartMarker}} пиши естественными русскими
        словами для одного русского голоса: каждое слово или фразу не на кириллице
        передавай русской фонетической записью. Например: Gemini Live -> Джемини Лайв,
        YouTube -> Ютуб, GPT -> джи пи ти. Не оставляй там латиницу, URL, Markdown,
        названия инструментов и сами маркеры. Не читай ссылку посимвольно: назови ее
        естественно, если это уместно.

        В части сразу после каждого {{TextStartMarker}} пиши обычный текст для чата с
        исходным написанием названий, ссылок и аббревиатур. Используй маркеры только в
        этих парах и не добавляй ничего после последней видимой части.
        """;

    private void Drain(List<SpeechFirstResponseChunk> emitted)
    {
        while (true)
        {
            switch (_state)
            {
                case ParseState.AwaitingSpeechMarker:
                    if (!TryConsumeSpeechMarker(emitted)) return;
                    continue;

                case ParseState.Speech:
                    if (!DrainSpeech(emitted)) return;
                    continue;

                case ParseState.Text:
                    if (!DrainText(emitted)) return;
                    continue;

                case ParseState.LegacyText:
                    if (_buffer.Length > 0)
                    {
                        Emit(emitted, SpeechFirstResponsePart.Text, _buffer.ToString());
                        _buffer.Clear();
                    }
                    return;
            }
        }
    }

    private bool TryConsumeSpeechMarker(List<SpeechFirstResponseChunk> emitted)
    {
        var value = _buffer.ToString();
        var firstNonWhitespace = 0;
        while (firstNonWhitespace < value.Length && char.IsWhiteSpace(value[firstNonWhitespace])) firstNonWhitespace++;
        var candidate = value[firstNonWhitespace..];

        if (candidate.StartsWith(SpeechStartMarker, StringComparison.Ordinal))
        {
            _buffer.Clear();
            _buffer.Append(candidate[SpeechStartMarker.Length..]);
            _state = ParseState.Speech;
            return true;
        }

        if (SpeechStartMarker.StartsWith(candidate, StringComparison.Ordinal))
        {
            // Keep only a possible marker prefix; leading newlines from a
            // provider do not belong in either user-visible representation.
            _buffer.Clear();
            _buffer.Append(candidate);
            return false;
        }

        // Old clients and an occasional non-conforming model answer still
        // work: expose it as a normal text stream, and mobile uses its
        // established plain-text TTS fallback.
        _buffer.Clear();
        _state = ParseState.LegacyText;
        Emit(emitted, SpeechFirstResponsePart.Text, value);
        return false;
    }

    private bool DrainSpeech(List<SpeechFirstResponseChunk> emitted)
    {
        var value = _buffer.ToString();
        var markerAt = value.IndexOf(TextStartMarker, StringComparison.Ordinal);
        if (markerAt >= 0)
        {
            EmitSpeech(emitted, value[..markerAt]);
            _buffer.Clear();
            _buffer.Append(value[(markerAt + TextStartMarker.Length)..]);
            _state = ParseState.Text;
            return true;
        }

        var retainedPrefixLength = LongestSuffixThatStartsMarker(value, TextStartMarker);
        var safeLength = value.Length - retainedPrefixLength;
        if (safeLength > 0)
        {
            EmitSpeech(emitted, value[..safeLength]);
            _buffer.Clear();
            _buffer.Append(value[safeLength..]);
        }
        return false;
    }

    // The current protocol deliberately interleaves speech and display
    // blocks sentence by sentence. Keep a possible partial speech marker in
    // the buffer just as DrainSpeech does for a partial display marker; that
    // lets a Gemini chunk split a marker at any character without leaking it
    // into the visible reply.
    private bool DrainText(List<SpeechFirstResponseChunk> emitted)
    {
        var value = _buffer.ToString();
        var markerAt = value.IndexOf(SpeechStartMarker, StringComparison.Ordinal);
        if (markerAt >= 0)
        {
            Emit(emitted, SpeechFirstResponsePart.Text, value[..markerAt]);
            _buffer.Clear();
            _buffer.Append(value[(markerAt + SpeechStartMarker.Length)..]);
            _state = ParseState.Speech;
            return true;
        }

        var retainedPrefixLength = LongestSuffixThatStartsMarker(value, SpeechStartMarker);
        var safeLength = value.Length - retainedPrefixLength;
        if (safeLength > 0)
        {
            Emit(emitted, SpeechFirstResponsePart.Text, value[..safeLength]);
            _buffer.Clear();
            _buffer.Append(value[safeLength..]);
        }
        return false;
    }

    private static int LongestSuffixThatStartsMarker(string value, string marker)
    {
        var max = Math.Min(value.Length, marker.Length - 1);
        for (var length = max; length > 0; length--)
        {
            if (value.EndsWith(marker[..length], StringComparison.Ordinal)) return length;
        }
        return 0;
    }

    private void EmitSpeech(List<SpeechFirstResponseChunk> emitted, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        _speech.Append(value);
        HasSpeech = true;
        emitted.Add(new SpeechFirstResponseChunk(SpeechFirstResponsePart.Speech, value));
    }

    private void Emit(List<SpeechFirstResponseChunk> emitted, SpeechFirstResponsePart part, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (part == SpeechFirstResponsePart.Text) HasText = true;
        emitted.Add(new SpeechFirstResponseChunk(part, value));
    }
}
