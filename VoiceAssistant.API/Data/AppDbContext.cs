using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using VoiceAssistant.API.Data.Entities;

namespace VoiceAssistant.API.Data;

public class AppDbContext : IdentityDbContext<User>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<UserSettings> UserSettings => Set<UserSettings>();
    public DbSet<SpeakerProfile> SpeakerProfiles => Set<SpeakerProfile>();
    public DbSet<DeviceLinkCode> DeviceLinkCodes => Set<DeviceLinkCode>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<User>(e =>
        {
            e.Property(u => u.NativeLang).HasMaxLength(5);
            e.Property(u => u.Level).HasMaxLength(5);
        });

        builder.Entity<ChatSession>(e =>
        {
            e.HasOne(s => s.User).WithMany(u => u.ChatSessions)
                .HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(s => s.Mode).HasMaxLength(20);
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
            e.Property(s => s.FullTranslation).HasDefaultValue(false);
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
    }
}
