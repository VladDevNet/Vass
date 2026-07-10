using VoiceAssistant.API.Data.Entities;
using VoiceAssistant.API.Services;
using Xunit;

namespace VoiceAssistant.API.Tests;

public class ConversationMemoryServiceTests
{
    private static Message Msg(int id, string content) =>
        new() { Id = id, Content = content, Role = "user", ChatSessionId = 1 };

    [Fact]
    public void UnsummarizedCharCount_SumsOnlyMessagesAfterCursor()
    {
        var messages = new List<Message>
        {
            Msg(1, new string('a', 100)),
            Msg(2, new string('b', 200)),
            Msg(3, new string('c', 300)),
        };

        var count = ConversationMemoryService.UnsummarizedCharCount(messages, lastSummarizedMessageId: 1);

        Assert.Equal(500, count);
    }

    [Fact]
    public void UnsummarizedCharCount_NullCursor_CountsAllMessages()
    {
        var messages = new List<Message> { Msg(1, new string('a', 100)), Msg(2, new string('b', 200)) };

        var count = ConversationMemoryService.UnsummarizedCharCount(messages, lastSummarizedMessageId: null);

        Assert.Equal(300, count);
    }

    [Fact]
    public void ShouldSummarize_BelowThreshold_ReturnsFalse()
    {
        var messages = new List<Message> { Msg(1, new string('a', ConversationMemoryService.SummarizationThresholdChars - 1)) };

        Assert.False(ConversationMemoryService.ShouldSummarize(messages, lastSummarizedMessageId: null));
    }

    [Fact]
    public void ShouldSummarize_AtThreshold_ReturnsTrue()
    {
        var messages = new List<Message> { Msg(1, new string('a', ConversationMemoryService.SummarizationThresholdChars)) };

        Assert.True(ConversationMemoryService.ShouldSummarize(messages, lastSummarizedMessageId: null));
    }

    [Fact]
    public void ShouldCompact_AtThreshold_ReturnsFalse()
    {
        var summary = new string('a', ConversationMemoryService.CompactionThresholdChars);

        Assert.False(ConversationMemoryService.ShouldCompact(summary));
    }

    [Fact]
    public void ShouldCompact_AboveThreshold_ReturnsTrue()
    {
        var summary = new string('a', ConversationMemoryService.CompactionThresholdChars + 1);

        Assert.True(ConversationMemoryService.ShouldCompact(summary));
    }

    [Fact]
    public void ShouldCompact_NullSummary_ReturnsFalse()
    {
        Assert.False(ConversationMemoryService.ShouldCompact(null));
    }
}
