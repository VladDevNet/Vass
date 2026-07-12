using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using VoiceAssistant.API.Data.Entities;

namespace VoiceAssistant.API.Data;

public class AppDbContext : IdentityDbContext<User>
{
    private readonly byte[] _apiKeyEncryptionKey;

    public AppDbContext(DbContextOptions<AppDbContext> options, IConfiguration configuration) : base(options)
    {
        _apiKeyEncryptionKey = ApiKeyEncryption.DeriveKey(configuration["Encryption:Key"]!);
    }

    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<UserSettings> UserSettings => Set<UserSettings>();
    public DbSet<SpeakerProfile> SpeakerProfiles => Set<SpeakerProfile>();
    public DbSet<DeviceLinkCode> DeviceLinkCodes => Set<DeviceLinkCode>();
    public DbSet<ClientLogEntry> ClientLogEntries => Set<ClientLogEntry>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<User>(e =>
        {
            e.Property(u => u.NativeLang).HasMaxLength(5);
            e.Property(u => u.Level).HasMaxLength(5);
            e.Property(u => u.IsApproved).HasDefaultValue(true);
        });

        builder.Entity<ChatSession>(e =>
        {
            e.HasOne(s => s.User).WithMany(u => u.ChatSessions)
                .HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(s => s.Mode).HasMaxLength(20);
            // Matches ChatController.MaxSessionTitleLength (PROJECT-AUDIT-2026-07-10 SEC-07).
            e.Property(s => s.Title).HasMaxLength(200);
        });

        builder.Entity<Message>(e =>
        {
            e.HasOne(m => m.ChatSession).WithMany(s => s.Messages)
                .HasForeignKey(m => m.ChatSessionId).OnDelete(DeleteBehavior.Cascade);
            e.Property(m => m.Role).HasMaxLength(10);
        });

        builder.Entity<UserSettings>(e =>
        {
            e.HasOne(s => s.User).WithOne()
                .HasForeignKey<UserSettings>(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => s.UserId).IsUnique();
            e.Property(s => s.InterfaceLanguage).HasMaxLength(5).HasDefaultValue("uk");
            e.Property(s => s.DisplayName).HasMaxLength(100);
            e.Property(s => s.AssistantName).HasMaxLength(100);
            e.Property(s => s.AvatarId).HasMaxLength(20);
            // Matches SettingsController.MaxCustomSystemPromptLength (PROJECT-AUDIT-2026-07-10 SEC-07).
            e.Property(s => s.CustomSystemPrompt).HasMaxLength(4000);
            e.Property(s => s.FullTranslation).HasDefaultValue(false);
            // PROJECT-AUDIT-2026-07-10 SEC-03: encrypted at rest, transparent
            // to every existing caller. See ApiKeyEncryptionConverter and
            // Program.cs's one-time startup migration for pre-existing rows.
            var keyConverter = new ApiKeyEncryptionConverter(_apiKeyEncryptionKey);
            e.Property(s => s.OpenAiApiKey).HasConversion(keyConverter);
            e.Property(s => s.AnthropicApiKey).HasConversion(keyConverter);
            e.Property(s => s.GeminiApiKey).HasConversion(keyConverter);
        });

        builder.Entity<SpeakerProfile>(e =>
        {
            e.Property(s => s.Name).HasMaxLength(100);
        });

        builder.Entity<DeviceLinkCode>(e =>
        {
            e.HasOne(d => d.User).WithMany()
                .HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(d => d.Code).HasMaxLength(6);
            e.HasIndex(d => d.Code).IsUnique();
        });

        builder.Entity<ClientLogEntry>(e =>
        {
            e.HasOne(l => l.User).WithMany()
                .HasForeignKey(l => l.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(l => l.RunId).HasMaxLength(40);
            e.Property(l => l.Level).HasMaxLength(10);
            e.Property(l => l.Category).HasMaxLength(20);
            // Query pattern is always "this user's most recent entries,
            // optionally one run" — matches how the mobile client tags and
            // how a debugging session would actually be reviewed.
            e.HasIndex(l => new { l.UserId, l.ClientTimestamp });
            e.HasIndex(l => l.RunId);
        });
    }
}
