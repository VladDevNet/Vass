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

    [Fact]
    public void HelpCatalog_UsesRuntimeCapabilities_AndProvidesInterfaceHints()
    {
        var registry = new AssistantCapabilityRegistry(new ConfigurationBuilder().Build());
        var fullContext = new AssistantRuntimeContext(
            HasVisualAttachment: false,
            SupportsScreenAnalysis: true,
            SupportsExternalActions: true,
            SupportsReminders: true,
            SupportsPeriodicReminders: true);
        var restrictedContext = fullContext with
        {
            SupportsScreenAnalysis = false,
            SupportsExternalActions = false
        };

        var fullHelp = registry.GetHelp(fullContext);
        var restrictedHelp = registry.GetHelp(restrictedContext);

        var memory = Assert.Single(fullHelp, item => item.Id == "memory");
        Assert.NotEmpty(memory.Examples);
        Assert.False(string.IsNullOrWhiteSpace(memory.InterfaceHint));
        Assert.Contains(fullHelp, item => item.Id == "screen");
        Assert.Contains(fullHelp, item => item.Id == "youtube");
        Assert.DoesNotContain(restrictedHelp, item => item.Id == "screen");
        Assert.DoesNotContain(restrictedHelp, item => item.Id == "youtube");
    }
}
