using VoiceAssistant.API.Services;
using Xunit;

namespace VoiceAssistant.API.Tests;

public class ReminderServiceTests
{
    [Theory]
    [InlineData("Напомни завтра позвонить врачу")]
    [InlineData("Поставь напоминание на вечер")]
    [InlineData("Нагадай мені подзвонити мамі")]
    public void MayContainReminder_ReminderPhrase_ReturnsTrue(string message)
    {
        Assert.True(ReminderService.MayContainReminder(message));
    }

    [Fact]
    public void MayContainReminder_OrdinaryConversation_ReturnsFalse()
    {
        Assert.False(ReminderService.MayContainReminder("Расскажи, какая завтра погода"));
    }

    [Fact]
    public void Parse_ValidJson_ReturnsTypedResult()
    {
        var parsed = ReminderService.Parse(
            "```json\n{\"isReminder\":true,\"needsClarification\":false,\"text\":\"позвонить врачу\",\"dueAtLocal\":\"2026-07-13T09:00:00\"}\n```");

        Assert.True(parsed.IsReminder);
        Assert.False(parsed.NeedsClarification);
        Assert.Equal("позвонить врачу", parsed.Text);
        Assert.Equal("2026-07-13T09:00:00", parsed.DueAtLocal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("NONE")]
    [InlineData("{not-json}")]
    public void Parse_InvalidContent_ReturnsNotReminder(string raw)
    {
        Assert.False(ReminderService.Parse(raw).IsReminder);
    }

    [Fact]
    public void TryConvertToUtc_WarsawSummerTime_UsesDstOffset()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

        var success = ReminderService.TryConvertToUtc("2026-07-13T09:00:00", zone, out var utc);

        Assert.True(success);
        Assert.Equal(new DateTime(2026, 7, 13, 7, 0, 0, DateTimeKind.Utc), utc);
    }

    [Fact]
    public void TryConvertToUtc_AmbiguousDstTime_ReturnsFalse()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

        Assert.False(ReminderService.TryConvertToUtc("2026-10-25T02:30:00", zone, out _));
    }

    [Theory]
    [InlineData("device-abc12345")]
    [InlineData("A1_b2.c3-d4")]
    public void IsValidDeviceId_ExpectedShape_ReturnsTrue(string deviceId)
    {
        Assert.True(ReminderService.IsValidDeviceId(deviceId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("bad device id")]
    public void IsValidDeviceId_InvalidShape_ReturnsFalse(string deviceId)
    {
        Assert.False(ReminderService.IsValidDeviceId(deviceId));
    }
}
