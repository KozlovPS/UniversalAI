
namespace UniversalAI.Core.Capabilities;

public enum TokenLimitParameter
{
    MaxTokens,
    MaxCompletionTokens
}

public class ModelCapabilities
{
    public bool SupportsTemperature { get; init; } = true;
    public bool SupportsTopP { get; init; } = true;
    public bool SupportsFrequencyPenalty { get; init; } = true;
    public bool SupportsPresencePenalty { get; init; } = true;
    public bool SupportsSeed { get; init; } = true;

    public bool SupportsReasoningEffort { get; init; }
    public bool SupportsReasoningBudgetTokens { get; init; }

    public TokenLimitParameter TokenLimitParameter { get; init; } =
        TokenLimitParameter.MaxTokens;
}
