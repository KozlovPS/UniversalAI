using System.Text.Json;
using UniversalAI.Core;
using UniversalAI.Services.Invariants;
using UniversalAI.Services.Providers;

namespace UniversalAI.Services.Agents;

public sealed class InvariantAgent(ConversationService conversations, IProviderResolver providers) : IAgent
{
    public string Id => "invariants";
    public string DisplayName => "Ассистент с инвариантами";
    public string Description => "Структурированные решения с проверкой кодом; /invariant set ключ = значение, list, audit.";

    public async Task<AgentTurnResult> RunAsync(int conversationId, string content, CancellationToken cancellationToken = default)
    {
        var c = await conversations.GetConversationAsync(conversationId) ?? throw new KeyNotFoundException("Диалог не найден.");
        var state = InvariantPolicy.Parse(c.InvariantStateJson);
        // Resolve lazily: commands work without a configured API key.
        var provider = content.StartsWith("/invariant", StringComparison.Ordinal) || state.Rules.Count == 0 ? null :
            await providers.GetProviderForConversationAsync(conversationId, cancellationToken);
        var turn = await InvariantPolicy.RunAsync(state, content, c.Model, () => provider!, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        c.InvariantStateJson = JsonSerializer.Serialize(turn.State);
        if (!ReferenceEquals(turn.State, state))
            c.ModulesJson = JsonSerializer.Serialize(ConversationModules.Resolve(c) with { Invariants = true });
        var raw = turn.Raw;
        var response = new ChatResult { Content = turn.Answer, FinishReason = "stop", PromptTokens = raw?.PromptTokens ?? 0,
            CompletionTokens = raw?.CompletionTokens ?? 0, TotalTokens = raw?.TotalTokens ?? 0 };
        await conversations.SaveMessagesAsync(conversationId, [new() { Role = "user", Content = content },
            new() { Role = "assistant", Content = turn.Answer, UsageModel = raw == null ? null : c.Model,
                UsagePromptTokens = raw?.PromptTokens, UsageCompletionTokens = raw?.CompletionTokens }], cancellationToken);
        return new(provider?.Name ?? "local", c.Model, response, 0, raw == null ? [] :
            [new(1, raw.FinishReason, raw.PromptTokens, raw.CompletionTokens, raw.TotalTokens, [])
                { EstimatedPromptTokens = TokenUsageCalculator.EstimateRequest(turn.Request!) }]);
    }
}
