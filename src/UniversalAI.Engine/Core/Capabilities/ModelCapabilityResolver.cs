
namespace UniversalAI.Core.Capabilities;

public static class ModelCapabilityResolver
{
    public static ModelCapabilities Resolve(
        string providerName,
        string model)
    {
        string id = model.Trim().ToLowerInvariant();


        if (id == "anthropic/claude-opus-5-5")
        {
            return new ModelCapabilities
            {
                SupportsTemperature = false,
                SupportsTopP = false,
                SupportsFrequencyPenalty = false,
                SupportsPresencePenalty = false,
                SupportsSeed = false,

                SupportsReasoningEffort = true,
                SupportsReasoningBudgetTokens = false,

                TokenLimitParameter = TokenLimitParameter.MaxTokens
            };
        }


        if (id is "gpt-6-luna" or "openai/gpt-6-luna")
        {
            return new ModelCapabilities
            {
                SupportsTemperature = false,
                SupportsTopP = false,
                SupportsFrequencyPenalty = false,
                SupportsPresencePenalty = false,
                SupportsSeed = false,

                SupportsReasoningEffort = true,
                SupportsReasoningBudgetTokens = true,

                TokenLimitParameter =
                    TokenLimitParameter.MaxCompletionTokens
            };
        }

        if (id is "gpt-6-sol" or "openai/gpt-6-sol" or
            "gpt-6-astra" or "openai/gpt-6-astra")
        {
            return new ModelCapabilities
            {
                SupportsTemperature = false,
                SupportsTopP = false,
                SupportsFrequencyPenalty = false,
                SupportsPresencePenalty = false,
                SupportsSeed = false,

                SupportsReasoningEffort = true,
                SupportsReasoningBudgetTokens = false,

                TokenLimitParameter =
                    TokenLimitParameter.MaxCompletionTokens
            };
        }


        if (id == "deepseek/deepseek-v4.1-flash")
        {
            return new ModelCapabilities
            {
                SupportsTemperature = false,
                SupportsTopP = false,
                SupportsFrequencyPenalty = false,
                SupportsPresencePenalty = false,
                SupportsSeed = false,

                SupportsReasoningEffort = true,
                SupportsReasoningBudgetTokens = false,

                TokenLimitParameter = TokenLimitParameter.MaxTokens
            };
        }

        // Профили ниже относятся к моделям OpenAI.
        // Не применяем их к Claude, Gemini и другим семействам.
        if (id.StartsWith("openai/"))
        {
            id = id["openai/".Length..];
        }
        else if (!providerName.Equals(
            "OpenAI",
            StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Для модели '{model}' не задан профиль возможностей. " +
                "Добавь профиль перед отправкой запроса.");
        }

        return id == "gpt-4o" ||
            id.StartsWith("gpt-4o-") ||
            id == "gpt-4o-mini" ||
            id.StartsWith("gpt-4o-mini-")
            ? new ModelCapabilities()
            : id == "o1" || id.StartsWith("o1-") ||
            id == "o3" || id.StartsWith("o3-") ||
            id == "o4-mini" || id.StartsWith("o4-mini-")
            ? new ModelCapabilities
            {
                SupportsTemperature = false,
                SupportsTopP = false,
                SupportsFrequencyPenalty = false,
                SupportsPresencePenalty = false,
                SupportsSeed = false,
                SupportsReasoningEffort = true,
                SupportsReasoningBudgetTokens = false,
                TokenLimitParameter =
                    TokenLimitParameter.MaxCompletionTokens
            }
            : throw new NotSupportedException(
            $"Для модели '{model}' пока нет профиля возможностей. " +
            "Неизвестные параметры нельзя безопасно отправлять автоматически.");
    }
}
