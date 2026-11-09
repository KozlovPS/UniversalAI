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
            ToolCalls: []);

        return new AgentTurnResult(
            turn.ProviderName,
            turn.Request.Model,
            turn.Response,
            ToolRounds: 0,
            ModelCalls: [trace]);
    }
}
