namespace UniversalAI.Services.Agents;

/// <summary>Token estimates and provider-reported usage for one conversation turn.</summary>
public sealed record AgentTokenUsage(
    int CurrentRequestEstimate,
    int HistoryEstimateBefore,
    int EstimatedPrompt,
    int HistoryEstimateAfter,
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    int MeasuredHistoryTurns,
    int? CumulativePromptTokens,
    int? CumulativeCompletionTokens,
    decimal? CostRubles,
    decimal? CumulativeCostRubles);

public sealed record ConversationTokenUsage(
    int MessageCount,
    int EstimatedHistoryTokens,
    int MeasuredTurns,
    int? PromptTokens,
    int? CompletionTokens,
    decimal? CostRubles);
