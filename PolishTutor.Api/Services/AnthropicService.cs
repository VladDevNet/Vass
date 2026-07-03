using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace PolishTutor.Api.Services;

public class AnthropicService
{
    private readonly AnthropicClient _defaultClient;
    private readonly ILogger<AnthropicService> _logger;

    public AnthropicService(IConfiguration config, ILogger<AnthropicService> logger)
    {
        _defaultClient = new AnthropicClient { ApiKey = config["Anthropic:ApiKey"]! };
        _logger = logger;
    }

    private AnthropicClient GetClient(string? apiKey) =>
        !string.IsNullOrWhiteSpace(apiKey) ? new AnthropicClient { ApiKey = apiKey } : _defaultClient;

    public async IAsyncEnumerable<string> StreamResponseAsync(
        string systemPrompt,
        List<MessageParam> messages,
        string model = "claude-haiku-4-5-20251001",
        int maxTokens = 2048,
        string? apiKey = null)
    {
        var client = GetClient(apiKey);
        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = maxTokens,
            System = systemPrompt,
            Messages = messages
        };

        await foreach (var rawEvent in client.Messages.CreateStreaming(parameters))
        {
            if (rawEvent.TryPickContentBlockDelta(out var delta))
            {
                if (delta.Delta.TryPickText(out var textDelta))
                {
                    yield return textDelta.Text;
                }
            }
        }
    }

    public async Task<string> CreateAsync(string systemPrompt, string userMessage,
        string model = "claude-haiku-4-5-20251001", int maxTokens = 1024, string? apiKey = null)
    {
        var client = GetClient(apiKey);
        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = model,
            MaxTokens = maxTokens,
            System = systemPrompt,
            Messages = [new MessageParam { Role = Role.User, Content = userMessage }]
        });

        return string.Join("", response.Content
            .Where(b => b.TryPickText(out _))
            .Select(b => { b.TryPickText(out var t); return t!.Text; }));
    }

    public async IAsyncEnumerable<string> ChatWithToolsAsync(
        string systemPrompt,
        List<MessageParam> messages,
        List<Tool> tools,
        Func<string, Dictionary<string, JsonElement>, Task<string>> toolExecutor,
        string model = "claude-haiku-4-5-20251001",
        int maxToolRounds = 10,
        string? apiKey = null)
    {
        var client = GetClient(apiKey);
        var toolUnions = tools.Select(t => (ToolUnion)t).ToList();

        for (var round = 0; round < maxToolRounds; round++)
        {
            var response = await client.Messages.Create(new MessageCreateParams
            {
                Model = model,
                MaxTokens = 2048,
                System = systemPrompt,
                Messages = messages,
                Tools = toolUnions
            });

            if (response.StopReason == StopReason.ToolUse)
            {
                var assistantBlocks = new List<ContentBlockParam>();
                foreach (var block in response.Content)
                {
                    if (block.TryPickText(out var textBlock))
                        assistantBlocks.Add(new TextBlockParam { Text = textBlock.Text });
                    else if (block.TryPickToolUse(out var toolUse))
                        assistantBlocks.Add(new ToolUseBlockParam { ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input });
                }

                messages.Add(new MessageParam { Role = Role.Assistant, Content = assistantBlocks });

                var toolResults = new List<ContentBlockParam>();
                foreach (var block in response.Content)
                {
                    if (block.TryPickToolUse(out var toolUse))
                    {
                        _logger.LogInformation("Tool call: {Tool}", toolUse.Name);
                        var input = new Dictionary<string, JsonElement>(toolUse.Input);
                        var result = await toolExecutor(toolUse.Name, input);

                        toolResults.Add(new ToolResultBlockParam
                        {
                            ToolUseID = toolUse.ID,
                            Content = result
                        });
                    }
                }

                messages.Add(new MessageParam { Role = Role.User, Content = toolResults });
                continue;
            }

            foreach (var block in response.Content)
            {
                if (block.TryPickText(out var text))
                {
                    yield return text.Text;
                }
            }
            yield break;
        }

        _logger.LogWarning("Max tool rounds ({Max}) reached", maxToolRounds);
        yield return "Przepraszam, coś poszło nie tak. Spróbuj jeszcze raz.";
    }
}
