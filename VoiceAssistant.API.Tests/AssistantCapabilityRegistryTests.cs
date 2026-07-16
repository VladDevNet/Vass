using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using VoiceAssistant.API.Data;
using VoiceAssistant.API.Data.Entities;
using VoiceAssistant.API.Services;

namespace VoiceAssistant.API.Tests;

public class AssistantCapabilityRegistryTests
{
    [Fact]
    public void CapabilitySnapshot_IsNotConstrainedToFourThousandCharacters()
    {
        var registry = new AssistantCapabilityRegistry(new ConfigurationBuilder().Build());
        var snapshot = registry.GetSnapshot(new AssistantRuntimeContext(
            HasVisualAttachment: true,
            SupportsScreenAnalysis: true,
            SupportsExternalActions: true,
            SupportsReminders: true));

        var serialized = AssistantCapabilityRegistry.SerializeContentFreeSnapshot(snapshot);

        Assert.True(serialized.Length > 4000);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Encryption:Key"] = "test-encryption-key-2026" })
            .Build();
        using var db = new AppDbContext(options, configuration);

        var property = db.Model.FindEntityType(typeof(Message))!
            .FindProperty(nameof(Message.CapabilitySnapshotJson))!;
        Assert.Null(property.GetMaxLength());
    }

    [Fact]
    public void PeriodicReminderCapability_RequiresProtocolV2Client()
    {
        var registry = new AssistantCapabilityRegistry(new ConfigurationBuilder().Build());
        var unsupported = registry.GetSnapshot(new AssistantRuntimeContext(
            HasVisualAttachment: false,
            SupportsScreenAnalysis: false,
            SupportsExternalActions: false,
            SupportsReminders: true));
        var supported = registry.GetSnapshot(new AssistantRuntimeContext(
            HasVisualAttachment: false,
            SupportsScreenAnalysis: false,
            SupportsExternalActions: false,
            SupportsReminders: true,
            SupportsPeriodicReminders: true));

        Assert.Equal("unsupported_client", unsupported.Capabilities.Single(item => item.Id == "reminder.periodic").State);
        Assert.Equal("available", supported.Capabilities.Single(item => item.Id == "reminder.periodic").State);
    }
}
