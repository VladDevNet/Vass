using VoiceAssistant.API.Services;

namespace VoiceAssistant.API.Tests;

public class AudioAnalysisServiceTests
{
    [Fact]
    public void RemovePathologicalRepetition_PreservesSpeechBeforeModelLoop()
    {
        var transcription = "Пожалуйста, напомни мне завтра позвонить маме. сейчас делают сейчас делают сейчас делают сейчас делают сейчас делают сейчас делают";

        var result = AudioAnalysisService.RemovePathologicalRepetition(transcription);

        Assert.Equal("Пожалуйста, напомни мне завтра позвонить маме.", result);
    }

    [Fact]
    public void RemovePathologicalRepetition_DiscardsPureLoop()
    {
        var transcription = "Я не знаю, что это такое. Я не знаю, что это такое. Я не знаю, что это такое. Я не знаю, что это такое.";

        var result = AudioAnalysisService.RemovePathologicalRepetition(transcription);

        Assert.Null(result);
    }

    [Fact]
    public void RemovePathologicalRepetition_KeepsOrdinaryEmphasis()
    {
        var transcription = "Я очень, очень хочу посмотреть это видео сегодня";

        var result = AudioAnalysisService.RemovePathologicalRepetition(transcription);

        Assert.Equal(transcription, result);
    }
}
