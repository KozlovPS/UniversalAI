
namespace UniversalAI.Core.Capabilities;

public static class ChatRequestValidator
{
    public static void Validate(
        ChatRequest request,
        ModelCapabilities capabilities)
    {
        List<string> unsupported = [];

        if (request.TopP.HasValue &&
            !capabilities.SupportsTopP)
        {
            unsupported.Add("top_p");
        }

        if (request.FrequencyPenalty.HasValue &&
            !capabilities.SupportsFrequencyPenalty)
        {
            unsupported.Add("frequency_penalty");
        }

        if (request.PresencePenalty.HasValue &&
            !capabilities.SupportsPresencePenalty)
        {
            unsupported.Add("presence_penalty");
        }

        if (request.Seed.HasValue &&
            !capabilities.SupportsSeed)
        {
            unsupported.Add("seed");
        }

        if (!string.IsNullOrWhiteSpace(request.ReasoningEffort) &&
            !capabilities.SupportsReasoningEffort)
        {
            unsupported.Add("reasoning_effort");
        }

        if (request.ReasoningBudgetTokens.HasValue &&
            !capabilities.SupportsReasoningBudgetTokens)
        {
            unsupported.Add("reasoning_budget_tokens");
        }

        if (request.MaxTokens is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.MaxTokens),
                "Лимит токенов должен быть положительным.");
        }


        string modelId = request.Model.Trim().ToLowerInvariant();

        bool isGpt6Luna =
            modelId is "gpt-6-luna" or "openai/gpt-6-luna";

        bool isDeepSeek =
            modelId == "deepseek/deepseek-v4.1-flash";

        if (!string.IsNullOrWhiteSpace(request.ReasoningEffort))
        {
            string effort =
                request.ReasoningEffort.Trim().ToLowerInvariant();

            bool valid = isGpt6Luna
                ? effort is "none" or "low" or "medium"
                    or "high" or "xhigh" or "max"
                : isDeepSeek
                    ? effort is "none" or "low" or "high" or "max"
                    : effort is "low" or "medium" or "high";

            if (!valid)
            {
                throw new ArgumentException(
                    $"Недопустимый reasoning_effort '{effort}' " +
                    $"для модели '{request.Model}'.");
            }
        }


        if (unsupported.Count > 0)
        {
            throw new NotSupportedException(
                $"Модель '{request.Model}' не поддерживает: " +
                string.Join(", ", unsupported));
        }
    }
}
