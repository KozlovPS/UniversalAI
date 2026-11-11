using UniversalAI.Services;

namespace UniversalAI.Services.Agents;

/// <summary>Runs one ordinary model request without offering tools.</summary>
public sealed class DirectChatAgent : IAgent
{
    private readonly ChatService _chat;

    public DirectChatAgent(ChatService chat)
    {
        _chat = chat;
    }

    public string Id => "direct";

    public string DisplayName => "Прямой чат";

    public string Description => "Один запрос к модели без вызова инструментов.";

    public async Task<AgentTurnResult> RunAsync(
        int conversationId,
        string content,
        CancellationToken cancellationToken = default)
    {
        ChatTurnResult turn = await _chat.SendMessageAsync(
            conversationId,
            content,
            cancellationToken);

        AgentModelCallTrace trace = new(
            Number: 1,
            FinishReason: turn.Response.FinishReason,
            PromptTokens: turn.Response.PromptTokens,
            CompletionTokens: turn.Response.CompletionTokens,
            TotalTokens: turn.Response.TotalTokens,
            ToolCalls: [])
        {
            EstimatedPromptTokens = TokenUsageCalculator.EstimateRequest(turn.Request)
        };

        List<UniversalAI.Core.ChatMessage> historyBefore = turn.Request.Messages
            .Skip(1)
            .Take(Math.Max(0, turn.Request.Messages.Count - 2))
            .ToList();
        UniversalAI.Core.ChatMessage savedUser = new()
        {
            Role = "user",
            Content = content
        };
        UniversalAI.Core.ChatMessage savedAssistant = new()
        {
            Role = "assistant",
            Content = turn.Response.Content,
            UsageModel = turn.Response.PromptTokens > 0 || turn.Response.CompletionTokens > 0
                ? turn.Request.Model
                : null,
            UsagePromptTokens = turn.Response.PromptTokens > 0 || turn.Response.CompletionTokens > 0
                ? turn.Response.PromptTokens
                : null,
            UsageCompletionTokens = turn.Response.PromptTokens > 0 || turn.Response.CompletionTokens > 0
                ? turn.Response.CompletionTokens
                : null
        };
        AgentTokenUsage tokenUsage = TokenUsageCalculator.CreateTurnUsage(
            turn.Request.Model,
            content,
            historyBefore,
            [.. historyBefore, savedUser, savedAssistant],
            trace.EstimatedPromptTokens,
            turn.Response.PromptTokens,
            turn.Response.CompletionTokens,
            turn.Response.TotalTokens);

        return new AgentTurnResult(
            turn.ProviderName,
            turn.Request.Model,
            turn.Response,
            ToolRounds: 0,
            ModelCalls: [trace])
        {
            TokenUsage = tokenUsage
        };
    }
}
