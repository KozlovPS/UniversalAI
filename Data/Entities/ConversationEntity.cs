namespace UniversalAI.Data.Entities;

public class ConversationEntity

{
    // Максимальное количество токенов ответа
    public int MaxTokens { get; set; } = 10000;

    // Top P
    public double? TopP { get; set; }

    // Штраф за частотность повторений
    public double? FrequencyPenalty { get; set; }

    // Штраф за повторное упоминание тем
    public double? PresencePenalty { get; set; }

    // Seed для воспроизводимости, если поддерживается
    public int? Seed { get; set; }

    // Уровень рассуждений: null, low, medium или high
    public string? ReasoningEffort { get; set; }

    // Бюджет токенов на рассуждения
    public int? ReasoningBudgetTokens { get; set; }

    public string SystemPrompt { get; set; } = "Ты полезный AI-ассистент. Отвечай на русском языке.";

    public string Model { get; set; } = "GigaChat-2";

    public double Temperature { get; set; } = 0.7;

    public int Id { get; set; }

    public string Title { get; set; } = "Новый диалог";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<ChatMessageEntity> Messages { get; set; } = [];

    public int? ProviderConnectionId { get; set; }

    public ProviderConnectionEntity? ProviderConnection { get; set; }
}