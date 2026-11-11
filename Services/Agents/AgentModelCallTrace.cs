namespace UniversalAI.Services.Agents;

public sealed record AgentModelCallTrace(
    int Number,
    string FinishReason,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    IReadOnlyList<AgentToolCallTrace> ToolCalls)
{
    public int EstimatedPromptTokens { get; init; }
}

public sealed record AgentToolCallTrace(
    string Name,
    string Arguments,
    string Result);
