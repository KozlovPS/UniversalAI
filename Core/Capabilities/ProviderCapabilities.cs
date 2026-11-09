namespace UniversalAI.Core.Capabilities;

public class ProviderCapabilities
{
    public bool SupportsTemperature { get; set; }

    public bool SupportsMaxTokens { get; set; }

    public bool SupportsTopP { get; set; }

    public bool SupportsFrequencyPenalty { get; set; }

    public bool SupportsPresencePenalty { get; set; }

    public bool SupportsSeed { get; set; }

    public bool SupportsReasoningEffort { get; set; }

    public bool SupportsReasoningBudgetTokens { get; set; }

    public bool SupportsJsonSchema { get; set; }

    public bool SupportsStreaming { get; set; }

    public bool SupportsFunctionCalling { get; set; }
}
