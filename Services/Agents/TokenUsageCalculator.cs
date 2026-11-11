using System.Globalization;
using System.Text;
using System.Text.Json;
using UniversalAI.Core;

namespace UniversalAI.Services.Agents;

/// <summary>
/// Provides a language-aware estimate for context breakdowns. The model/API
/// usage values remain the source of truth for billed token counts.
/// </summary>
public static class TokenUsageCalculator
{
    private const decimal TokensPerMillion = 1_000_000m;

    // Price snapshot from BENCHMARK-DAY-5.md (2026-11-08), rubles per 1M tokens.
    private static readonly IReadOnlyDictionary<string, (decimal Input, decimal Output)> Prices =
        new Dictionary<string, (decimal, decimal)>(StringComparer.OrdinalIgnoreCase)
        {
            ["deepseek/deepseek-v4.1-flash"] = (26.07m, 100.25m),
            ["gpt-6-sol"] = (420m, 2_100m),
            ["gpt-6-astra"] = (1_580m, 7_900m)
        };

    public static int EstimateText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        int tokens = 0;
        int asciiWordLength = 0;
        int nonAsciiWordLength = 0;

        void FlushWords()
        {
            tokens += (asciiWordLength + 3) / 4;
            tokens += (nonAsciiWordLength + 1) / 2;
            asciiWordLength = 0;
            nonAsciiWordLength = 0;
        }

        foreach (Rune rune in text.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                if (rune.IsAscii)
                {
                    if (nonAsciiWordLength > 0)
                    {
                        FlushWords();
                    }

                    asciiWordLength++;
                }
                else
                {
                    if (asciiWordLength > 0)
                    {
                        FlushWords();
                    }

                    nonAsciiWordLength++;
                }

                continue;
            }

            FlushWords();
            if (!Rune.IsWhiteSpace(rune))
            {
                // Punctuation, emoji, and other symbols usually consume a token.
                tokens++;
            }
        }

        FlushWords();
        return tokens;
    }

    public static int EstimateMessages(IEnumerable<ChatMessage> messages)
    {
        int tokens = 0;
        foreach (ChatMessage message in messages)
        {
            // Role/message framing varies by provider and model; four is a small
            // fixed allowance for each message boundary and role.
            tokens += 4 + EstimateText(message.Role) + EstimateText(message.Content);
            if (message.ToolCallId != null)
            {
                tokens += EstimateText(message.ToolCallId);
            }

            if (message.FunctionName != null)
            {
                tokens += EstimateText(message.FunctionName);
            }

            if (message.ToolCalls is { Count: > 0 })
            {
                foreach (ChatToolCall toolCall in message.ToolCalls)
                {
                    tokens += 4 + EstimateText(toolCall.Id) +
                              EstimateText(toolCall.Function.Name) +
                              EstimateText(toolCall.Function.Arguments);
                }
            }
        }

        return tokens;
    }

    public static int EstimateRequest(ChatRequest request)
    {
        int tokens = EstimateMessages(request.Messages);
        if (request.Tools.Count > 0)
        {
            string toolsJson = JsonSerializer.Serialize(request.Tools);
            tokens += EstimateText(toolsJson) + 12 * request.Tools.Count;
        }

        return tokens;
    }

    public static AgentTokenUsage CreateTurnUsage(
        string model,
        string currentRequest,
        IReadOnlyCollection<ChatMessage> historyBefore,
        IEnumerable<ChatMessage> historyAfter,
        int estimatedPrompt,
        int promptTokens,
        int completionTokens,
        int totalTokens)
    {
        ConversationTokenUsage prior = SummarizeHistory(historyBefore);
        bool usageAvailable = promptTokens > 0 || completionTokens > 0 || totalTokens > 0;
        int? currentPrompt = usageAvailable ? promptTokens : null;
        int? currentCompletion = usageAvailable ? completionTokens : null;
        int? currentTotal = usageAvailable
            ? totalTokens > 0 ? totalTokens : promptTokens + completionTokens
            : null;
        decimal? currentCost = usageAvailable
            ? EstimateCost(model, promptTokens, completionTokens)
            : null;

        int? cumulativePrompt = currentPrompt.HasValue && prior.PromptTokens.HasValue
            ? currentPrompt.Value + prior.PromptTokens.Value
            : null;
        int? cumulativeCompletion = currentCompletion.HasValue && prior.CompletionTokens.HasValue
            ? currentCompletion.Value + prior.CompletionTokens.Value
            : null;

        decimal? cumulativeCost = currentCost.HasValue && prior.CostRubles.HasValue
            ? currentCost.Value + prior.CostRubles.Value
            : null;

        return new AgentTokenUsage(
            CurrentRequestEstimate: EstimateText(currentRequest) + EstimateText("user") + 4,
            HistoryEstimateBefore: EstimateMessages(historyBefore),
            EstimatedPrompt: estimatedPrompt,
            HistoryEstimateAfter: EstimateMessages(historyAfter),
            PromptTokens: currentPrompt,
            CompletionTokens: currentCompletion,
            TotalTokens: currentTotal,
            MeasuredHistoryTurns: prior.MeasuredTurns,
            CumulativePromptTokens: cumulativePrompt,
            CumulativeCompletionTokens: cumulativeCompletion,
            CostRubles: currentCost,
            CumulativeCostRubles: cumulativeCost);
    }

    public static ConversationTokenUsage SummarizeHistory(
        IReadOnlyCollection<ChatMessage> history)
    {
        ChatMessage[] measuredTurns = history
            .Where(message => message.Role == "assistant" &&
                              message.UsagePromptTokens.HasValue &&
                              message.UsageCompletionTokens.HasValue)
            .ToArray();

        int? promptTokens = measuredTurns.Length == 0
            ? 0
            : measuredTurns.Sum(message => message.UsagePromptTokens!.Value);
        int? completionTokens = measuredTurns.Length == 0
            ? 0
            : measuredTurns.Sum(message => message.UsageCompletionTokens!.Value);

        decimal? cost = 0m;
        foreach (ChatMessage message in measuredTurns)
        {
            decimal? messageCost = EstimateCost(
                message.UsageModel,
                message.UsagePromptTokens!.Value,
                message.UsageCompletionTokens!.Value);
            if (!messageCost.HasValue)
            {
                cost = null;
                break;
            }

            cost += messageCost.Value;
        }

        return new ConversationTokenUsage(
            MessageCount: history.Count,
            EstimatedHistoryTokens: EstimateMessages(history),
            MeasuredTurns: measuredTurns.Length,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            CostRubles: cost);
    }

    public static decimal? EstimateCost(
        string? model,
        int promptTokens,
        int completionTokens)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        string normalized = model.Trim();
        if (normalized.StartsWith("openai/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["openai/".Length..];
        }

        if (!Prices.TryGetValue(normalized, out (decimal Input, decimal Output) price))
        {
            return null;
        }

        return (promptTokens * price.Input + completionTokens * price.Output) /
               TokensPerMillion;
    }

    public static bool IsContextLimitError(string message)
    {
        string lower = message.ToLower(CultureInfo.InvariantCulture);
        return lower.Contains("context_length", StringComparison.Ordinal) ||
               lower.Contains("context_window", StringComparison.Ordinal) ||
               lower.Contains("context length", StringComparison.Ordinal) ||
               lower.Contains("context window", StringComparison.Ordinal) ||
               lower.Contains("maximum context", StringComparison.Ordinal) ||
               lower.Contains("maximum sequence", StringComparison.Ordinal) ||
               lower.Contains("prompt is too long", StringComparison.Ordinal) ||
               lower.Contains("too many tokens", StringComparison.Ordinal) ||
               lower.Contains("token limit", StringComparison.Ordinal) ||
               lower.Contains("контекст", StringComparison.Ordinal) ||
               lower.Contains("лимит токенов", StringComparison.Ordinal) ||
               lower.Contains("превышен лимит", StringComparison.Ordinal) ||
               lower.Contains("превышает лимит", StringComparison.Ordinal);
    }
}

public sealed class ModelContextLimitException : Exception
{
    public ModelContextLimitException(
        string model,
        int estimatedPromptTokens,
        Exception innerException)
        : base(
            $"Модель {model} отклонила запрос из-за лимита контекста. " +
            $"Локальная оценка промпта: около {estimatedPromptTokens} токенов. " +
            "Оценка приблизительная; точный лимит задаёт модель.",
            innerException)
    {
        Model = model;
        EstimatedPromptTokens = estimatedPromptTokens;
    }

    public string Model { get; }

    public int EstimatedPromptTokens { get; }
}
