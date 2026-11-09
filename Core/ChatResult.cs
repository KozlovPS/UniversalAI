namespace UniversalAI.Core;

public class ChatResult
{
    public string Content { get; set; } = "";

    public string FinishReason { get; set; } = "unknown";

    public int PromptTokens { get; set; }

    public int CompletionTokens { get; set; }

    public int TotalTokens { get; set; }

    public List<ChatToolCall> ToolCalls { get; set; } = [];

    public string? FunctionsStateId { get; set; }
}
