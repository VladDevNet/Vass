using VoiceAssistant.API.Services;
using Xunit;

namespace VoiceAssistant.API.Tests;

public class LongTermMemoryServiceTests
{
    [Fact]
    public void ParseFacts_ValidJson_NormalizesDeduplicatesAndLimits()
    {
        var raw = """
            {"facts":["  Пользователь любит   чай  ","пользователь любит чай","Факт 2","Факт 3","Факт 4"]}
            """;

        var facts = LongTermMemoryService.ParseFacts(raw);

        Assert.Equal(3, facts.Count);
        Assert.Equal("Пользователь любит чай", facts[0]);
        Assert.Equal("Факт 2", facts[1]);
        Assert.Equal("Факт 3", facts[2]);
    }

    [Fact]
    public void ParseFacts_CodeFence_ParsesPayload()
    {
        var facts = LongTermMemoryService.ParseFacts("```json\n{\"facts\":[\"Внука зовут Миша\"]}\n```");

        Assert.Equal(["Внука зовут Миша"], facts);
    }

    [Theory]
    [InlineData("")]
    [InlineData("NONE")]
    [InlineData("{not json}")]
    [InlineData("{\"facts\":\"not-array\"}")]
    public void ParseFacts_InvalidPayload_ReturnsEmpty(string raw)
    {
        Assert.Empty(LongTermMemoryService.ParseFacts(raw));
    }

    [Fact]
    public void ComputeContentHash_IgnoresCaseAndWhitespace()
    {
        var first = LongTermMemoryService.ComputeContentHash(" Пользователь  любит чай ");
        var second = LongTermMemoryService.ComputeContentHash("пользователь любит ЧАЙ");

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
    }
}
