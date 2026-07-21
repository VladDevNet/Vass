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

    [Fact]
    public void DiscoverableHelpCatalog_ExcludesOrdinaryConversation_AndKeepsOnlyTrackableCapabilities()
    {
        var registry = new AssistantCapabilityRegistry(new ConfigurationBuilder().Build());
        var context = new AssistantRuntimeContext(
            HasVisualAttachment: false,
            SupportsScreenAnalysis: true,
            SupportsExternalActions: true,
            SupportsReminders: true,
            SupportsPeriodicReminders: true,
            SupportsLibrary: true);

        var discoverable = registry.GetDiscoverableHelp(context);

        Assert.DoesNotContain(discoverable, item => item.Id == "conversation");
        Assert.All(discoverable, item => Assert.True(AssistantCapabilityRegistry.IsDiscoverableHelpId(item.Id)));
        Assert.Contains(discoverable, item => item.Id == "memory");
        Assert.Contains(discoverable, item => item.Id == "overlay");
    }

    [Fact]
    public void LibraryCapability_IsClientBound_AndCatalogIsMarkedUntrusted()
    {
        var registry = new AssistantCapabilityRegistry(new ConfigurationBuilder().Build());
        var context = new AssistantRuntimeContext(
            HasVisualAttachment: false,
            SupportsScreenAnalysis: false,
            SupportsExternalActions: true,
            SupportsReminders: false,
            SupportsLibrary: true,
            LibraryCatalog:
            [
                new AssistantLibraryCatalogItem(
                    "cfb4dfe9-0fae-45b9-a1fa-5a4701cb854b",
                    "Ужин на неделю",
                    "recipes",
                    "Пять простых рецептов",
                    2,
                    "Кулинария")
            ],
            LibrarySections:
            [
                new AssistantLibrarySectionItem(
                    "a0a1950c-02f1-4fc9-8e95-f97c6bf3e8ee",
                    "Кулинария")
            ]);

        var available = registry.GetSnapshot(context);
        var unavailable = registry.GetSnapshot(context with { SupportsLibrary = false });
        var manifest = registry.BuildPromptManifest(context);
        var help = registry.GetHelp(context);

        Assert.Equal("available", available.Capabilities.Single(item => item.Id == "library.write").State);
        Assert.Equal("unavailable", unavailable.Capabilities.Single(item => item.Id == "library.write").State);
        Assert.Contains(help, item => item.Id == "library");
        Assert.Contains("недоверенные метаданные", manifest);
        Assert.Contains("cfb4dfe9-0fae-45b9-a1fa-5a4701cb854b", manifest);
        Assert.Contains("sectionTitle", manifest);
        Assert.Contains("sections", manifest);
    }
}
