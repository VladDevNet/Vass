using System.Diagnostics;
using System.Text.Json;

namespace VoiceAssistant.API.Services;

// OpenAI's native function-calling loop. The model only proposes declared
// functions; AssistantToolBroker remains the sole executor and validator.
public sealed class OpenAiAssistantAgentTurnService
{
    private readonly OpenAiResponsesService _openAi;
    private readonly AssistantToolBroker _broker;
    private readonly ILogger<OpenAiAssistantAgentTurnService> _logger;

    public OpenAiAssistantAgentTurnService(
        OpenAiResponsesService openAi,
        AssistantToolBroker broker,
        ILogger<OpenAiAssistantAgentTurnService> logger)
    {
        _openAi = openAi;
        _broker = broker;
        _logger = logger;
    }

    public async Task<AssistantAgentTurnResult> RunAsync(
        string systemPrompt,
        IReadOnlyList<GeminiMessage> messages,
        string? geminiApiKey,
        string userId,
        int sourceMessageId,
        AssistantRuntimeContext context,
        GroundedWebSearchPrefetch? webSearchPrefetch,
        CancellationToken cancellationToken)
    {
        var input = OpenAiResponsesService.BuildInput(messages).ToList();
        if (input.Count == 0)
            return new(false, [], null, false, false, false);

        var tools = BuildTools();
        var executions = new List<AssistantToolExecution>();
        var usedTools = false;
        using var timeBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeBudget.CancelAfter(AssistantAgentTurnService.MaxTurnDuration);
        var ct = timeBudget.Token;
        try
        {
            for (var step = 0; step < AssistantAgentTurnService.MaxModelSteps; step++)
            {
                var plannerStopwatch = Stopwatch.StartNew();
                var proposal = await _openAi.CreateResponseAsync(
                    AssistantToolPlannerService.BuildPlannerInstructions(systemPrompt), input, tools, 4096, ct);
                plannerStopwatch.Stop();
                _logger.LogInformation(
                    "OpenAI assistant agent planner completed: Step {Step}; Duration {DurationMs}ms; Calls {CallCount}",
                    step + 1, plannerStopwatch.ElapsedMilliseconds, proposal.FunctionCalls.Count);

                if (proposal.FunctionCalls.Count == 0)
                    return new(usedTools, executions, usedTools ? proposal.Text?.Trim() : null, false, false, false);

                usedTools = true;
                var calls = proposal.FunctionCalls
                    .Select(call => new AssistantToolCall(call.Name, ParseArguments(call.Arguments), call.CallId))
                    .ToArray();
                var callsForStep = calls.Any(call => call.Name == "screen_capture_once")
                    ? calls.Where(call => call.Name == "screen_capture_once").Take(1).ToArray()
                    : calls;

                var toolsStopwatch = Stopwatch.StartNew();
                var stepExecutions = await _broker.ExecuteAsync(
                    callsForStep,
                    userId,
                    sourceMessageId,
                    context with
                    {
                        HasProposedClientAction = executions.Any(execution => execution.ExternalAction is not null),
                        HasAttemptedReminder = executions.Any(execution => execution.Name is "reminder_create" or "periodic_reminder_create"),
                        HasAttemptedWebSearch = executions.Any(execution => execution.Name == "web_search")
                    },
                    geminiApiKey,
                    webSearchPrefetch,
                    ct);
                toolsStopwatch.Stop();
                _logger.LogInformation(
                    "OpenAI assistant agent tools completed: Step {Step}; Duration {DurationMs}ms; Calls {CallCount}; Names {ToolNames}",
                    step + 1, toolsStopwatch.ElapsedMilliseconds, callsForStep.Length,
                    string.Join(',', callsForStep.Select(call => call.Name).Distinct(StringComparer.Ordinal)));
                executions.AddRange(stepExecutions);

                if (AssistantAgentTurnService.TryGetDirectWebSearchFinalText(executions, callsForStep, stepExecutions, out var webSearchFinalText))
                    return new(true, executions, webSearchFinalText, false, false, false);
                if (stepExecutions.Any(execution => execution.RequestsScreenCapture))
                    return new(true, executions, null, true, false, false);

                // Responses requires the complete model output (including reasoning)
                // before function_call_output items so it can retain tool-call state.
                input.AddRange(proposal.Output);
                foreach (var execution in stepExecutions)
                {
                    if (!string.IsNullOrWhiteSpace(execution.CallId))
                        input.Add(JsonSerializer.SerializeToElement(new
                        {
                            type = "function_call_output",
                            call_id = execution.CallId,
                            output = SerializeToolOutput(execution)
                        }));
                }
            }

            _logger.LogWarning("OpenAI assistant agent turn reached the {MaxModelSteps}-step limit", AssistantAgentTurnService.MaxModelSteps);
            return new(true, executions, null, false, true, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeBudget.IsCancellationRequested)
        {
            _logger.LogWarning("OpenAI assistant agent turn exceeded its {DurationSeconds}-second time budget after {ToolCount} tool execution(s)",
                AssistantAgentTurnService.MaxTurnDuration.TotalSeconds, executions.Count);
            return new(usedTools, executions, null, false, false, true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenAI assistant agent turn failed after {ToolCount} tool execution(s)", executions.Count);
            return new(usedTools, executions, null, false, false, usedTools);
        }
    }

    private static IReadOnlyList<JsonElement> BuildTools() =>
        JsonSerializer.SerializeToElement(AssistantToolPlannerService.GetDeclarations())
            .EnumerateArray()
            .Select(declaration =>
            {
                var name = declaration.GetProperty("name").GetString();
                var description = declaration.GetProperty("description").GetString();
                var parameters = declaration.GetProperty("parameters").Clone();
                return JsonSerializer.SerializeToElement(new { type = "function", name, description, parameters });
            })
            .ToArray();

    private static JsonElement ParseArguments(string arguments)
    {
        try
        {
            using var document = JsonDocument.Parse(arguments);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.Clone()
                : JsonSerializer.SerializeToElement(new { });
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new { });
        }
    }

    private static string SerializeToolOutput(AssistantToolExecution execution) =>
        execution.Data?.GetRawText() ?? JsonSerializer.Serialize(new
        {
            status = execution.Status,
            summary = execution.Summary,
            actionId = execution.ActionReceiptId
        });
}
