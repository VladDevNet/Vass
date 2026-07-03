using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VoiceAssistant.API.Data;
using VoiceAssistant.API.Data.Entities;

namespace VoiceAssistant.API.Services;

public class TutorToolExecutor(AppDbContext db)
{
    public async Task<string> ExecuteAsync(string toolName, Dictionary<string, JsonElement> input, string userId, int sessionId)
    {
        return toolName switch
        {
            "get_learner_context" => await GetLearnerContext(userId),
            "lookup_vocabulary" => await LookupVocabulary(userId, input),
            "save_word" => await SaveWord(userId, input),
            "record_error" => await RecordError(userId, sessionId, input),
            "get_vocabulary_stats" => await GetVocabularyStats(userId),
            "get_weak_words" => await GetWeakWords(userId, input),
            "update_custom_instructions" => await UpdateCustomInstructions(userId, input),
            _ => JsonSerializer.Serialize(new { error = $"Unknown tool: {toolName}" })
        };
    }

    private async Task<string> GetLearnerContext(string userId)
    {
        var user = await db.Users.FindAsync(userId);
        if (user == null) return """{"error":"user not found"}""";

        var instruction = await db.TutorInstructions
            .FirstOrDefaultAsync(t => t.UserId == userId);

        var recentErrors = await db.LearnerErrors
            .Where(e => e.UserId == userId)
            .OrderByDescending(e => e.CreatedAt)
            .Take(10)
            .Select(e => new { e.ErrorType, e.Original, e.Corrected, e.GrammarTopic })
            .ToListAsync();

        var weakWords = await db.UserWords
            .Where(w => w.UserId == userId && (w.ErrorCount > 0 || w.Status == "new"))
            .OrderByDescending(w => w.ErrorCount)
            .Take(10)
            .Select(w => new { w.Word, w.Translation, w.Status, w.ErrorCount })
            .ToListAsync();

        return JsonSerializer.Serialize(new
        {
            level = user.Level,
            nativeLang = user.NativeLang,
            conductorInstructions = instruction?.InstructionsJson,
            recentErrors,
            weakWords
        });
    }

    private async Task<string> LookupVocabulary(string userId, Dictionary<string, JsonElement> input)
    {
        var word = input["word"].GetString()!.ToLowerInvariant();
        var uw = await db.UserWords
            .FirstOrDefaultAsync(w => w.UserId == userId && w.Word.ToLower() == word);

        if (uw == null) return """{"found":false}""";

        return JsonSerializer.Serialize(new
        {
            found = true,
            word = uw.Word,
            translation = uw.Translation,
            status = uw.Status,
            errorCount = uw.ErrorCount
        });
    }

    private async Task<string> SaveWord(string userId, Dictionary<string, JsonElement> input)
    {
        var word = input["word"].GetString()!;
        var translation = input["translation"].GetString()!;

        var exists = await db.UserWords
            .AnyAsync(w => w.UserId == userId && w.Word.ToLower() == word.ToLowerInvariant());

        if (exists) return """{"saved":false,"reason":"already exists"}""";

        db.UserWords.Add(new UserWord
        {
            UserId = userId,
            Word = word,
            Translation = translation,
            Status = "new"
        });
        await db.SaveChangesAsync();

        return """{"saved":true}""";
    }

    private async Task<string> RecordError(string userId, int sessionId, Dictionary<string, JsonElement> input)
    {
        var error = new LearnerError
        {
            UserId = userId,
            ChatSessionId = sessionId,
            Original = input["original"].GetString()!,
            Corrected = input["corrected"].GetString()!,
            ErrorType = input["errorType"].GetString()!,
            GrammarTopic = input.TryGetValue("grammarTopic", out var gt) ? gt.GetString() : null
        };
        db.LearnerErrors.Add(error);
        await db.SaveChangesAsync();

        return """{"recorded":true}""";
    }

    private async Task<string> GetVocabularyStats(string userId)
    {
        var stats = await db.UserWords
            .Where(w => w.UserId == userId)
            .GroupBy(w => w.Status)
            .Select(g => new { status = g.Key, count = g.Count() })
            .ToListAsync();

        var total = stats.Sum(s => s.count);

        return JsonSerializer.Serialize(new { total, byStatus = stats });
    }

    private async Task<string> GetWeakWords(string userId, Dictionary<string, JsonElement> input)
    {
        var limit = input.TryGetValue("limit", out var l) ? l.GetInt32() : 10;

        var words = await db.UserWords
            .Where(w => w.UserId == userId && (w.ErrorCount > 0 || w.Status == "new"))
            .OrderByDescending(w => w.ErrorCount)
            .Take(limit)
            .Select(w => new { w.Word, w.Translation, w.Status, w.ErrorCount })
            .ToListAsync();

        return JsonSerializer.Serialize(words);
    }

    private async Task<string> UpdateCustomInstructions(string userId, Dictionary<string, JsonElement> input)
    {
        var instructions = input["instructions"].GetString()!;

        var settings = await db.UserSettings.FirstOrDefaultAsync(s => s.UserId == userId);
        if (settings == null)
        {
            settings = new UserSettings 
            { 
                UserId = userId, 
                CustomSystemPrompt = instructions 
            };
            db.UserSettings.Add(settings);
        }
        else
        {
            settings.CustomSystemPrompt = instructions;
            settings.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
        return JsonSerializer.Serialize(new { updated = true });
    }
}
