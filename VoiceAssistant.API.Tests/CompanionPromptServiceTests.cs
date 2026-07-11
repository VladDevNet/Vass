using VoiceAssistant.API.Services;
using Xunit;

namespace VoiceAssistant.API.Tests;

public class CompanionPromptServiceTests
{
    private static readonly DateTime FixedNow = new(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void BuildSystemPrompt_WithoutSummary_OmitsMemoryBlock()
    {
        var result = CompanionPromptService.BuildSystemPrompt("base", FixedNow);

        Assert.DoesNotContain("Память о более ранней части разговора", result);
    }

    [Fact]
    public void BuildSystemPrompt_WithSummary_AppendsMemoryBlock()
    {
        var result = CompanionPromptService.BuildSystemPrompt("base", FixedNow, mediumTermSummary: "пользователь любит чай");

        Assert.Contains("## Память о более ранней части разговора:\nпользователь любит чай", result);
    }

    [Fact]
    public void BuildSystemPrompt_IncludesBasePromptAndDate()
    {
        var result = CompanionPromptService.BuildSystemPrompt("BASE_MARKER", FixedNow);

        Assert.Contains("BASE_MARKER", result);
        Assert.Contains("2026-07-10", result);
    }

    [Fact]
    public void BuildSystemPrompt_EmptySummary_OmitsMemoryBlock()
    {
        var result = CompanionPromptService.BuildSystemPrompt("base", FixedNow, mediumTermSummary: "");

        Assert.DoesNotContain("Память о более ранней части разговора", result);
    }
}
