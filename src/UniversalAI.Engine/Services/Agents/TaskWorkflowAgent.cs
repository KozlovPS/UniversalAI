using UniversalAI.Core;
using UniversalAI.Providers;
using UniversalAI.Services.Providers;
using UniversalAI.Services.Tasks;

namespace UniversalAI.Services.Agents;

public sealed class TaskWorkflowAgent(ConversationService conversations, IProviderResolver providers,
    ContextCompressionService contextService) : IAgent
{
    public string Id => "task";
    public string DisplayName => "Агент с состоянием задачи";
    public string Description => "План → утверждение пользователя → реализация → валидация → финал. Пауза и продолжение.";

    public async Task<AgentTurnResult> RunAsync(int conversationId, string content, CancellationToken cancellationToken = default)
    {
        var c = await conversations.GetConversationAsync(conversationId) ?? throw new KeyNotFoundException("Диалог не найден.");
        var state = TaskStateMachine.Parse(c.TaskStateJson);
        var history = await conversations.GetMessagesAsync(conversationId, cancellationToken);
        IAIProvider provider = await providers.GetProviderForConversationAsync(conversationId, cancellationToken);
        PreparedContext? prepared = null;
        var turn = await TaskWorkflow.RunAsync(state, content, c.Model, provider, cancellationToken, async () =>
        {
            prepared = await contextService.PrepareAsync(c, history, provider, cancellationToken, currentUserText: content);
            return [new ChatMessage { Role = "system", Content = c.SystemPrompt }, .. prepared.Messages];
        }, messages => new ChatRequest { Model = c.Model, Messages = messages, Temperature = c.Temperature,
            MaxTokens = c.MaxTokens, TopP = c.TopP, FrequencyPenalty = c.FrequencyPenalty,
            PresencePenalty = c.PresencePenalty, Seed = c.Seed, ReasoningEffort = c.ReasoningEffort,
            ReasoningBudgetTokens = c.ReasoningBudgetTokens });
        int input = turn.Calls.Sum(call => call.Response.PromptTokens);
        int output = turn.Calls.Sum(call => call.Response.CompletionTokens);
        int total = turn.Calls.Sum(call => call.Response.TotalTokens);
        bool usage = input > 0 || output > 0;
        List<ChatMessage> saved = [new() { Role = "user", Content = content }, new()
        { Role = "assistant", Content = turn.Answer, UsageModel = usage ? c.Model : null,
            UsagePromptTokens = usage ? input : null, UsageCompletionTokens = usage ? output : null }];
        prepared ??= new([], TokenUsageCalculator.EstimateMessages(history), 0, 0, 0, false, null);
        if (turn.Allowed && !ReferenceEquals(turn.Before, turn.After))
            prepared = prepared with { PendingTaskState = turn.After, ClearTaskState = turn.After == null };
        await conversations.SaveMessagesAsync(conversationId, saved, cancellationToken, prepared);
        var traces = turn.Calls.Select((call, index) => new AgentModelCallTrace(index + 1,
            call.Response.FinishReason, call.Response.PromptTokens, call.Response.CompletionTokens,
            call.Response.TotalTokens, call.Response.ToolCalls.Select(t => new AgentToolCallTrace(t.Function.Name,
                t.Function.Arguments, turn.Reason)).ToList()) { EstimatedPromptTokens = TokenUsageCalculator.EstimateRequest(call.Request) }).ToList();
        return new(provider.Name, c.Model, new() { Content = turn.Answer, FinishReason = "stop",
            PromptTokens = input, CompletionTokens = output, TotalTokens = total }, 0, traces)
        {
            Context = prepared,
            Task = new(turn.Before, turn.After, turn.RequestedAction, turn.AppliedAction, turn.Allowed, turn.Reason),
            TokenUsage = TokenUsageCalculator.CreateTurnUsage(c.Model, content, history, [.. history, .. saved],
                traces.Sum(t => t.EstimatedPromptTokens), input, output, total)
        };
    }
}

public sealed record TaskTurnTrace(TaskWorkflowState? Before, TaskWorkflowState? After, string RequestedAction,
    string AppliedAction, bool Allowed, string Reason);
