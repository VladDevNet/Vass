using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using Pgvector.EntityFrameworkCore;
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
    public DbSet<MemoryFact> MemoryFacts => Set<MemoryFact>();
    public DbSet<MemoryItem> MemoryItems => Set<MemoryItem>();
    public DbSet<MemoryOperation> MemoryOperations => Set<MemoryOperation>();
    public DbSet<ActionReceipt> ActionReceipts => Set<ActionReceipt>();
    public DbSet<Reminder> Reminders => Set<Reminder>();
    public DbSet<ReminderDelivery> ReminderDeliveries => Set<ReminderDelivery>();
    public DbSet<VisualAsset> VisualAssets => Set<VisualAsset>();
    public DbSet<MessageAttachment> MessageAttachments => Set<MessageAttachment>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        if (Database.IsNpgsql())
        {
            builder.HasPostgresExtension("vector");
        }

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

        builder.Entity<VisualAsset>(e =>
        {
            e.HasOne(a => a.User).WithMany(u => u.VisualAssets)
                .HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(a => a.StorageFileName).HasMaxLength(80);
            e.Property(a => a.MimeType).HasMaxLength(50);
            e.Property(a => a.OriginalFileName).HasMaxLength(255);
            e.HasIndex(a => a.StorageFileName).IsUnique();
            e.HasIndex(a => new { a.UserId, a.CreatedAt });
        });

        builder.Entity<MessageAttachment>(e =>
        {
            e.HasOne(a => a.Message).WithMany(m => m.Attachments)
                .HasForeignKey(a => a.MessageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(a => a.VisualAsset).WithMany(v => v.MessageAttachments)
                .HasForeignKey(a => a.VisualAssetId).OnDelete(DeleteBehavior.Cascade);
            e.Property(a => a.Kind).HasMaxLength(20);
            e.HasIndex(a => new { a.MessageId, a.VisualAssetId }).IsUnique();
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

        builder.Entity<MemoryFact>(e =>
        {
            e.HasOne(m => m.User).WithMany()
                .HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(m => m.Fact).HasMaxLength(1000);
            e.Property(m => m.ContentHash).HasMaxLength(64);
            e.Property(m => m.EmbeddingModel).HasMaxLength(50);
            e.Property(m => m.IsActive).HasDefaultValue(true);
            e.HasIndex(m => new { m.UserId, m.ContentHash }).IsUnique();
            e.HasIndex(m => new { m.UserId, m.IsActive });

            if (Database.IsNpgsql())
            {
                e.Property(m => m.Embedding).HasColumnType("vector(768)");
                e.HasIndex(m => m.Embedding)
                    .HasMethod("hnsw")
                    .HasOperators("vector_cosine_ops");
            }
            else
            {
                // The integration suite uses SQLite. Keep the current model
                // creatable there while production retains a real vector column.
                e.Property(m => m.Embedding).HasConversion(new ValueConverter<Vector, byte[]>(
                    value => VectorToBytes(value),
                    value => BytesToVector(value)));
            }
        });

        builder.Entity<MemoryItem>(e =>
        {
            e.HasOne(m => m.User).WithMany(u => u.MemoryItems)
                .HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(m => m.Kind).HasMaxLength(40);
            e.Property(m => m.Category).HasMaxLength(40).HasDefaultValue(MemoryCategories.Other);
            e.Property(m => m.Text).HasMaxLength(1000);
            e.Property(m => m.ContentHash).HasMaxLength(64);
            e.Property(m => m.Status).HasMaxLength(20);
            e.Property(m => m.EmbeddingModel).HasMaxLength(50);
            e.Property(m => m.EmbeddingState).HasMaxLength(20);
            e.HasIndex(m => new { m.UserId, m.ContentHash }).IsUnique();
            e.HasIndex(m => new { m.UserId, m.Status, m.UpdatedAt });
            e.HasIndex(m => new { m.UserId, m.Category, m.Status, m.UpdatedAt });
            e.HasIndex(m => m.LegacyMemoryFactId).IsUnique();
            e.HasOne(m => m.VisualAsset).WithMany(asset => asset.MemoryItems)
                .HasForeignKey(m => m.VisualAssetId).OnDelete(DeleteBehavior.Restrict);

            if (Database.IsNpgsql())
            {
                e.Property(m => m.Embedding).HasColumnType("vector(768)");
                e.HasIndex(m => m.Embedding)
                    .HasMethod("hnsw")
                    .HasOperators("vector_cosine_ops");
            }
            else
            {
                e.Property(m => m.Embedding).HasConversion(new ValueConverter<Vector?, byte[]?>(
                    value => value == null ? null : VectorToBytes(value),
                    value => value == null ? null : BytesToVector(value)));
            }
        });

        builder.Entity<MemoryOperation>(e =>
        {
            e.HasOne(o => o.User).WithMany()
                .HasForeignKey(o => o.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(o => o.Operation).HasMaxLength(30);
            e.Property(o => o.ArgumentsHash).HasMaxLength(64);
            e.Property(o => o.Status).HasMaxLength(30);
            e.Property(o => o.ResultCode).HasMaxLength(40);
            e.Property(o => o.ConfirmationTokenHash).HasMaxLength(64);
            e.HasIndex(o => new { o.UserId, o.CreatedAt });
        });

        builder.Entity<ActionReceipt>(e =>
        {
            e.HasOne(receipt => receipt.User).WithMany()
                .HasForeignKey(receipt => receipt.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(receipt => receipt.ActionType).HasMaxLength(40);
            e.Property(receipt => receipt.Taxonomy).HasMaxLength(30);
            e.Property(receipt => receipt.Status).HasMaxLength(30);
            e.Property(receipt => receipt.ResultCode).HasMaxLength(64);
            e.HasIndex(receipt => new { receipt.UserId, receipt.CreatedAt });
            e.HasIndex(receipt => new { receipt.SourceMessageId, receipt.CreatedAt });
        });

        builder.Entity<Reminder>(e =>
        {
            e.HasOne(r => r.User).WithMany()
                .HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(r => r.Text).HasMaxLength(500);
            e.Property(r => r.TimeZoneId).HasMaxLength(100);
            e.Property(r => r.RecurrenceRule).HasMaxLength(200);
            e.Property(r => r.Status).HasMaxLength(20);
            e.Property(r => r.CreatedByDeviceId).HasMaxLength(64);
            e.HasIndex(r => new { r.UserId, r.Status, r.DueAtUtc });
            e.HasIndex(r => new { r.UserId, r.OperationId }).IsUnique();
        });

        builder.Entity<ReminderDelivery>(e =>
        {
            e.HasOne(d => d.Reminder).WithMany(r => r.Deliveries)
                .HasForeignKey(d => d.ReminderId).OnDelete(DeleteBehavior.Cascade);
            e.Property(d => d.DeviceId).HasMaxLength(64);
            e.Property(d => d.Status).HasMaxLength(20);
            e.Property(d => d.LocalNotificationId).HasMaxLength(200);
            e.Property(d => d.Error).HasMaxLength(500);
            e.HasIndex(d => new { d.ReminderId, d.DeviceId }).IsUnique();
        });
    }

    private static byte[] VectorToBytes(Vector vector)
    {
        var values = vector.ToArray();
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static Vector BytesToVector(byte[] bytes)
    {
        var values = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return new Vector(values);
    }
}
