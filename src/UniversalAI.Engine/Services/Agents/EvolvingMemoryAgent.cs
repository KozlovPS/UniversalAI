using System.Text.Json;
using UniversalAI.Services.Context;
using UniversalAI.Services.Providers;

namespace UniversalAI.Services.Agents;

public sealed class EvolvingMemoryAgent(ConversationService conversations, IProviderResolver providers) : IAgent
{
    public string Id => "evolving";
    public string DisplayName => "Обновляемая память";
    public string Description => "Явная /remember JSON-запись: replace, append, task; list, audit, complete. Только актуальный контекст.";
    public async Task<AgentTurnResult> RunAsync(int conversationId, string content, CancellationToken cancellationToken = default)
    {
        var c = await conversations.GetConversationAsync(conversationId) ?? throw new KeyNotFoundException("Диалог не найден.");
        var provider = content.StartsWith("/remember ", StringComparison.Ordinal) ? null :
            await providers.GetProviderForConversationAsync(conversationId, cancellationToken);
        var turn = await EvolvingMemory.RunAsync(EvolvingMemory.Parse(c.EvolvingMemoryJson), c.WorkingMemoryJson,
            content, c.Model, () => provider!, cancellationToken);
        c.EvolvingMemoryJson = JsonSerializer.Serialize(turn.State, EvolvingMemory.Json);
        if (content.Trim() == "/remember complete") c.WorkingMemoryJson = "{}";
        var raw = turn.Raw;
        await conversations.SaveMessagesAsync(conversationId, [new() { Role = "user", Content = content },
            new() { Role = "assistant", Content = turn.Answer, UsageModel = raw == null ? null : c.Model,
                UsagePromptTokens = raw?.PromptTokens, UsageCompletionTokens = raw?.CompletionTokens }], cancellationToken);
        return new(provider?.Name ?? "local", c.Model, raw ?? new() { Content = turn.Answer, FinishReason = "stop" }, 0,
            raw == null ? [] : [new(1, raw.FinishReason, raw.PromptTokens, raw.CompletionTokens, raw.TotalTokens, [])
                { EstimatedPromptTokens = TokenUsageCalculator.EstimateRequest(turn.Request!) }]);
    }
}
