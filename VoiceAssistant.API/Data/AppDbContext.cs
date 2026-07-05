using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using VoiceAssistant.API.Data.Entities;

namespace VoiceAssistant.API.Data;

public class AppDbContext : IdentityDbContext<User>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Lesson> Lessons => Set<Lesson>();
    public DbSet<Exercise> Exercises => Set<Exercise>();
    public DbSet<TestResult> TestResults => Set<TestResult>();
    public DbSet<LearningPlan> LearningPlans => Set<LearningPlan>();
    public DbSet<UserWord> UserWords => Set<UserWord>();
    public DbSet<TutorInstruction> TutorInstructions => Set<TutorInstruction>();
    public DbSet<LearnerError> LearnerErrors => Set<LearnerError>();
    public DbSet<UserSettings> UserSettings => Set<UserSettings>();
    public DbSet<SpeakerProfile> SpeakerProfiles => Set<SpeakerProfile>();

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

        builder.Entity<Lesson>(e =>
        {
            e.Property(l => l.Level).HasMaxLength(5);
        });

        builder.Entity<Exercise>(e =>
        {
            e.HasOne(ex => ex.Lesson).WithMany(l => l.Exercises)
                .HasForeignKey(ex => ex.LessonId).OnDelete(DeleteBehavior.Cascade);
            e.Property(ex => ex.Type).HasMaxLength(20);
        });

        builder.Entity<TestResult>(e =>
        {
            e.HasOne(t => t.User).WithMany(u => u.TestResults)
                .HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(t => t.TestType).HasMaxLength(20);
            e.Property(t => t.Level).HasMaxLength(5);
        });

        builder.Entity<LearningPlan>(e =>
        {
            e.HasOne(p => p.User).WithOne(u => u.LearningPlan)
                .HasForeignKey<LearningPlan>(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<UserWord>(e =>
        {
            e.HasOne(w => w.User).WithMany(u => u.UserWords)
                .HasForeignKey(w => w.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(w => w.Word).HasMaxLength(200);
            e.Property(w => w.Translation).HasMaxLength(500);
            e.Property(w => w.Status).HasMaxLength(20);
            e.HasIndex(w => new { w.UserId, w.Word }).IsUnique();
        });

        builder.Entity<TutorInstruction>(e =>
        {
            e.HasOne(t => t.User).WithOne(u => u.TutorInstruction)
                .HasForeignKey<TutorInstruction>(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(t => t.UserId).IsUnique();
        });

        builder.Entity<UserSettings>(e =>
        {
            e.HasOne(s => s.User).WithOne()
                .HasForeignKey<UserSettings>(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => s.UserId).IsUnique();
            e.Property(s => s.InterfaceLanguage).HasMaxLength(5).HasDefaultValue("uk");
            e.Property(s => s.DisplayName).HasMaxLength(100);
            e.Property(s => s.FullTranslation).HasDefaultValue(false);
        });

        builder.Entity<LearnerError>(e =>
        {
            e.HasOne(le => le.User).WithMany(u => u.LearnerErrors)
                .HasForeignKey(le => le.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(le => le.ChatSession).WithMany()
                .HasForeignKey(le => le.ChatSessionId).OnDelete(DeleteBehavior.NoAction);
            e.Property(le => le.ErrorType).HasMaxLength(20);
        });

        builder.Entity<SpeakerProfile>(e =>
        {
            e.Property(s => s.Name).HasMaxLength(100);
        });
    }
}
