using System.Text.Json;
using Anthropic.Models.Messages;

namespace VoiceAssistant.API.Services;

public class TutorTools
{
    private static readonly List<Tool> _tools;

    static TutorTools()
    {
        _tools =
        [
            MakeTool("get_learner_context",
                "Get learner profile: current level, native language, conductor instructions, recent errors, weak words.",
                new Dictionary<string, JsonElement>(),
                []),

            MakeTool("lookup_vocabulary",
                "Check if the learner already knows a specific word. Returns word info or 'not found'.",
                new Dictionary<string, JsonElement>
                {
                    ["word"] = JsonElement("string", "Polish word to look up")
                },
                ["word"]),

            MakeTool("save_word",
                "Add a new word to the learner's vocabulary. Skip if already exists.",
                new Dictionary<string, JsonElement>
                {
                    ["word"] = JsonElement("string", "Polish word"),
                    ["translation"] = JsonElement("string", "Translation in learner's native language")
                },
                ["word", "translation"]),

            MakeTool("record_error",
                "Record a learner's language error for tracking and analysis.",
                new Dictionary<string, JsonElement>
                {
                    ["original"] = JsonElement("string", "What the learner wrote/said"),
                    ["corrected"] = JsonElement("string", "Correct form"),
                    ["errorType"] = JsonElement("string", "Type of error: grammar, vocabulary, or spelling"),
                    ["grammarTopic"] = JsonElement("string", "Grammar topic if applicable")
                },
                ["original", "corrected", "errorType"]),

            MakeTool("get_vocabulary_stats",
                "Get learner's vocabulary statistics: counts by status (new, learning, known).",
                new Dictionary<string, JsonElement>(),
                []),

            MakeTool("get_weak_words",
                "Get words the learner struggles with most (high error count or status=new).",
                new Dictionary<string, JsonElement>
                {
                    ["limit"] = JsonElement("integer", "Max words to return (default 10)")
                },
                []),

            MakeTool("update_custom_instructions",
                "Update the learner's custom instructions or focus preferences (e.g., focus more on vocabulary, correct grammar less aggressively, speak slower, etc.). This persists across chats and sessions.",
                new Dictionary<string, JsonElement>
                {
                    ["instructions"] = JsonElement("string", "The new custom instructions or learning focus preferences to set (e.g. 'fokusuj się na nowych słowach, mniej na wymowie').")
                },
                ["instructions"]),
        ];
    }

    public List<Tool> GetTools() => _tools;

    private static Tool MakeTool(string name, string description,
        Dictionary<string, JsonElement> properties, string[] required)
    {
        return new Tool
        {
            Name = name,
            Description = description,
            InputSchema = new InputSchema
            {
                Type = Parse("\"object\""),
                Properties = properties,
                Required = required
            }
        };
    }

    private static JsonElement JsonElement(string type, string desc)
    {
        return Parse($$"""{"type":"{{type}}","description":"{{desc}}"}""");
    }

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
