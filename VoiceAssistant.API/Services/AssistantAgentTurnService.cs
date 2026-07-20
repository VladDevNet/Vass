using System.Text.Json;

namespace VoiceAssistant.API.Services;

public sealed record AssistantAgentTurnResult(
    bool UsedTools,
    IReadOnlyList<AssistantToolExecution> ToolExecutions,
    string? FinalText,
    bool RequestsScreenCapture,
    bool HitStepLimit,
    bool ProviderFailed);

// Executes the native provider function-calling protocol for one interactive
// turn. The loop is deliberately bounded: durable/reconnectable work belongs
// to the later AgentRun runtime, not to a request that can outlive its client.
public sealed class AssistantAgentTurnService
{
    public const int MaxModelSteps = 4;
    public static readonly TimeSpan MaxTurnDuration = TimeSpan.FromSeconds(25);

    private readonly AssistantToolPlannerService _planner;
    private readonly AssistantToolBroker _broker;
    private readonly ILogger<AssistantAgentTurnService> _logger;

    public AssistantAgentTurnService(
        AssistantToolPlannerService planner,
        AssistantToolBroker broker,
        ILogger<AssistantAgentTurnService> logger)
    {
        _planner = planner;
        _broker = broker;
        _logger = logger;
    }

    public async Task<AssistantAgentTurnResult> RunAsync(
        string systemPrompt,
        IReadOnlyList<GeminiMessage> messages,
        string? apiKey,
        string userId,
        int sourceMessageId,
        AssistantRuntimeContext context,
        bool supportsSpeechText,
        CancellationToken cancellationToken)
    {
        var contents = BuildInitialContents(messages);
        if (contents.Count == 0)
            return new(false, [], null, false, false, false);

        var executions = new List<AssistantToolExecution>();
        var usedTools = false;
        using var timeBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeBudget.CancelAfter(MaxTurnDuration);
        var turnCancellationToken = timeBudget.Token;
        try
        {
            for (var step = 0; step < MaxModelSteps; step++)
            {
                var proposal = await _planner.GenerateAsync(
                    systemPrompt,
                    contents,
                    apiKey,
                    turnCancellationToken,
                    emitSpeechFirstResponse: supportsSpeechText && usedTools);
                if (!proposal.ProviderAvailable || !proposal.HasModelContent)
                {
                    return new(usedTools, executions, null, false, false, ProviderFailed: usedTools);
                }

                if (proposal.Calls.Count == 0)
                {
                    // For an ordinary chat reply preserve the existing
                    // streamGenerateContent path with grounding. Once a tool
                    // has run, this is the model's final, evidence-aware reply.
                    return new(usedTools, executions, usedTools ? proposal.Text?.Trim() : null, false, false, false);
                }

                usedTools = true;
                // A capture is mediated input, not an ordinary parallel
                // action. Do not save memory or open a client surface before
                // Android has shown its one-shot consent dialog.
                var callsForStep = proposal.Calls.Any(call => call.Name == "screen_capture_once")
                    ? proposal.Calls.Where(call => call.Name == "screen_capture_once").Take(1).ToArray()
                    : proposal.Calls;
                var stepExecutions = await _broker.ExecuteAsync(
                    callsForStep,
                    userId,
                    sourceMessageId,
                    context with
                    {
                        HasProposedClientAction = executions.Any(execution => execution.ExternalAction is not null),
                        HasAttemptedReminder = executions.Any(execution =>
                            execution.Name is "reminder_create" or "periodic_reminder_create")
                    },
                    turnCancellationToken);
                executions.AddRange(stepExecutions);
                contents.Add(proposal.ModelContent!.Value);

                // Screen capture is mediated user input, not a background
                // tool result. The retry with the OS-approved image begins a
                // fresh turn and owns the eventual answer.
                if (stepExecutions.Any(execution => execution.RequestsScreenCapture))
                    return new(true, executions, null, true, false, false);

                contents.Add(CreateFunctionResponseContent(stepExecutions));
            }

            _logger.LogWarning("Assistant agent turn reached the {MaxModelSteps}-step limit", MaxModelSteps);
            return new(true, executions, null, false, true, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeBudget.IsCancellationRequested)
        {
            _logger.LogWarning("Assistant agent turn exceeded its {DurationSeconds}-second time budget after {ToolCount} tool execution(s)",
                MaxTurnDuration.TotalSeconds,
                executions.Count);
            return new(usedTools, executions, null, false, false, ProviderFailed: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Assistant agent turn failed after {ToolCount} tool execution(s)", executions.Count);
            return new(usedTools, executions, null, false, false, ProviderFailed: usedTools);
        }
    }

    // The tool-planning call sees the same current-turn attachment as the
    // ordinary streaming reply. Otherwise it can request a second screenshot
    // even though the user already supplied the image to analyse.
    internal static List<JsonElement> BuildInitialContents(IReadOnlyList<GeminiMessage> messages) =>
        messages
            .Where(message => message.Parts.Any(part => part.Data is not null || !string.IsNullOrWhiteSpace(part.Text)))
            .Select(message => JsonSerializer.SerializeToElement(new
            {
                role = message.Role.Equals("user", StringComparison.OrdinalIgnoreCase) ? "user" : "model",
                parts = GeminiService.SerializeParts(message.Parts)
            }))
            .ToList();

    private static JsonElement CreateFunctionResponseContent(IEnumerable<AssistantToolExecution> executions)
    {
        var parts = executions.Select(execution =>
        {
            var functionResponse = new Dictionary<string, object?>
            {
                ["name"] = execution.Name,
                ["response"] = execution.Data ?? JsonSerializer.SerializeToElement(new
                {
                    status = execution.Status,
                    summary = execution.Summary,
                    actionId = execution.ActionReceiptId
                })
            };
            if (!string.IsNullOrWhiteSpace(execution.CallId))
                functionResponse["id"] = execution.CallId;

            return new Dictionary<string, object?>
            {
                ["functionResponse"] = functionResponse
            };
        }).ToArray();

        return JsonSerializer.SerializeToElement(new { role = "user", parts });
    }
}
