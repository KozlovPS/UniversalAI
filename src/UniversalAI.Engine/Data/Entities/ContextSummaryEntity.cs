namespace UniversalAI.Data.Entities;

/// <summary>Derived context, stored separately from the original message log.</summary>
public sealed class ContextSummaryEntity
{
    public int ConversationId { get; set; }
    public string Content { get; set; } = "";
    public int CoveredMessageCount { get; set; }
    public int Revision { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public bool UsageComplete { get; set; } = true;
}
