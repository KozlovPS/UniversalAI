using UniversalAI.Core;

namespace UniversalAI.Services.Agents;

public sealed record AgentTurnResult(
    string ProviderName,
    string Model,
    ChatResult Response,
    int ToolRounds,
    IReadOnlyList<AgentModelCallTrace> ModelCalls);
