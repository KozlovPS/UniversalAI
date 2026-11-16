using System.Text.Json;

namespace UniversalAI.Core;

public class ChatRequest
{

    public string Model { get; set; } = "";

    public List<ChatMessage> Messages { get; set; } = [];

    public List<ChatFunctionDefinition> Tools { get; set; } = [];

    public int? MaxTokens { get; set; }

    public double? Temperature { get; set; }

    public JsonElement? ResponseFormat { get; set; }

    public double? TopP { get; set; }

    public double? FrequencyPenalty { get; set; }

    public double? PresencePenalty { get; set; }

    public int? Seed { get; set; }

    public string? ReasoningEffort { get; set; }

    public int? ReasoningBudgetTokens { get; set; }
}
