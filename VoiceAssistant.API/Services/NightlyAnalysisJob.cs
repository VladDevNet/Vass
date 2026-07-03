using System.Text;
using Microsoft.EntityFrameworkCore;
using VoiceAssistant.API.Data;
using VoiceAssistant.API.Data.Entities;

namespace VoiceAssistant.API.Services;

public class NightlyAnalysisJob(
    IServiceScopeFactory scopeFactory,
    AnthropicService anthropic,
    IWebHostEnvironment env,
    ILogger<NightlyAnalysisJob> logger) : BackgroundService
{
    private readonly string _promptTemplate = LoadPrompt(env);

    private static string LoadPrompt(IWebHostEnvironment env)
    {
        var path = Path.Combine(env.ContentRootPath, "Prompts", "conductor-analysis.txt");
        return File.Exists(path) ? File.ReadAllText(path) : "Analyze learner progress and return JSON.";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Wait until 03:00 UTC
            var now = DateTime.UtcNow;
            var next = now.Date.AddHours(3);
            if (next <= now) next = next.AddDays(1);
            var delay = next - now;

            logger.LogInformation("Nightly analysis scheduled in {Delay}", delay);
            await Task.Delay(delay, stoppingToken);

            try
            {
                await RunAnalysis(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Nightly analysis failed");
            }
        }
    }

    private async Task RunAnalysis(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var today = DateTime.UtcNow.Date;
        var activeUsers = await db.Users
            .Where(u => u.LastActiveAt >= today)
            .ToListAsync(ct);

        logger.LogInformation("Nightly analysis: {Count} active users", activeUsers.Count);

        foreach (var user in activeUsers)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await AnalyzeUser(db, user, today, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Analysis failed for user {UserId}", user.Id);
            }
        }
    }

    private async Task AnalyzeUser(AppDbContext db, User user, DateTime today, CancellationToken ct)
    {
        // Fetch today's sessions with messages
        var sessions = await db.ChatSessions
            .Where(s => s.UserId == user.Id && s.CreatedAt >= today)
            .Include(s => s.Messages)
            .ToListAsync(ct);

        if (sessions.Count == 0) return;

        var sessionSummaries = new StringBuilder();
        foreach (var s in sessions)
        {
            sessionSummaries.AppendLine($"Sesja ({s.Mode}): {s.Messages.Count} wiadomości");
            foreach (var m in s.Messages.OrderBy(m => m.CreatedAt).Take(5))
                sessionSummaries.AppendLine($"  [{m.Role}]: {Truncate(m.Content, 200)}");
            if (s.Messages.Count > 5) sessionSummaries.AppendLine("  ...");
        }

        // Fetch today's errors
        var errors = await db.LearnerErrors
            .Where(e => e.UserId == user.Id && e.CreatedAt >= today)
            .ToListAsync(ct);

        var errorsList = new StringBuilder();
        foreach (var e in errors)
            errorsList.AppendLine($"- [{e.ErrorType}] \"{e.Original}\" → \"{e.Corrected}\" {e.GrammarTopic}");

        // Vocabulary stats
        var words = await db.UserWords
            .Where(w => w.UserId == user.Id)
            .ToListAsync(ct);

        var total = words.Count;
        var newW = words.Count(w => w.Status == "new");
        var learning = words.Count(w => w.Status == "learning");
        var known = words.Count(w => w.Status == "known");
        var errorWords = string.Join(", ",
            words.Where(w => w.ErrorCount > 0)
                .OrderByDescending(w => w.ErrorCount)
                .Take(10)
                .Select(w => $"{w.Word} ({w.ErrorCount})"));

        // Current instructions
        var current = await db.TutorInstructions
            .FirstOrDefaultAsync(t => t.UserId == user.Id, ct);

        var nativeLang = user.NativeLang == "ru" ? "rosyjski" : "ukraiński";

        var prompt = _promptTemplate
            .Replace("{LEVEL}", user.Level)
            .Replace("{NATIVE_LANG}", nativeLang)
            .Replace("{SESSION_SUMMARIES}", sessionSummaries.ToString())
            .Replace("{ERRORS_LIST}", errorsList.Length > 0 ? errorsList.ToString() : "Brak błędów")
            .Replace("{TOTAL}", total.ToString())
            .Replace("{NEW}", newW.ToString())
            .Replace("{LEARNING}", learning.ToString())
            .Replace("{KNOWN}", known.ToString())
            .Replace("{ERROR_WORDS}", string.IsNullOrEmpty(errorWords) ? "Brak" : errorWords)
            .Replace("{CURRENT_INSTRUCTIONS}", current?.InstructionsJson ?? "Brak (pierwsza analiza)");

        // Call Sonnet (non-streaming)
        var messages = new List<Anthropic.Models.Messages.MessageParam>
        {
            new() { Role = Anthropic.Models.Messages.Role.User, Content = prompt }
        };

        var response = new StringBuilder();
        await foreach (var chunk in anthropic.StreamResponseAsync(
            "You are a learning coordinator. Return only valid JSON.",
            messages,
            model: "claude-sonnet-4-20250514",
            maxTokens: 1024))
        {
            response.Append(chunk);
        }

        var json = response.ToString().Trim();

        // Upsert TutorInstruction
        if (current != null)
        {
            current.InstructionsJson = json;
            current.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            db.TutorInstructions.Add(new TutorInstruction
            {
                UserId = user.Id,
                InstructionsJson = json
            });
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Updated instructions for user {UserId}", user.Id);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
