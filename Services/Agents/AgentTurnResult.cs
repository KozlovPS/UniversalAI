using UniversalAI.Core;

namespace UniversalAI.Services.Agents;

public sealed record AgentTurnResult(
    string ProviderName,
    string Model,
    ChatResult Response,
    int ToolRounds,
    IReadOnlyList<AgentModelCallTrace> ModelCalls)
{
    public AgentTokenUsage TokenUsage { get; init; } = new(
        0, 0, 0, 0, null, null, null, 0, null, null, null, null);
}
