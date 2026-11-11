using System.Globalization;
using UniversalAI.Services;
using UniversalAI.Services.Agents;

namespace UniversalAI.ConsoleApp.Commands;

public sealed class TokenStatisticsCommandHandler : IConsoleCommandHandler
{
    private readonly ConversationService _conversations;

    public TokenStatisticsCommandHandler(ConversationService conversations)
    {
        _conversations = conversations;
    }

    public async Task<bool> TryHandleAsync(
        string input,
        ConsoleChatSession session,
        CancellationToken cancellationToken = default)
    {
        if (!input.Equals("/tokens", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        List<UniversalAI.Core.ChatMessage> history =
            await _conversations.GetMessagesAsync(
                session.ConversationId,
                cancellationToken);
        ConversationTokenUsage usage = TokenUsageCalculator.SummarizeHistory(history);

        Console.WriteLine("\n--- Статистика токенов диалога ---");
        Console.WriteLine($"Сообщений в истории: {usage.MessageCount}");
        Console.WriteLine($"История (оценка): около {usage.EstimatedHistoryTokens} токенов");
        Console.WriteLine($"Ходов с сохранённым usage API: {usage.MeasuredTurns}");
        Console.WriteLine(
            $"Записанные токены API: prompt {FormatCount(usage.PromptTokens)}, " +
            $"completion {FormatCount(usage.CompletionTokens)}");
        Console.WriteLine($"Расчётная стоимость: {FormatCost(usage.CostRubles)}");
        Console.WriteLine("Счётчик оценивает текст истории; стоимость учитывает только сохранённые usage и известные тарифы.");
        return true;
    }

    private static string FormatCount(int? value) => value?.ToString() ?? "н/д";

    private static string FormatCost(decimal? value) => value.HasValue
        ? value.Value.ToString("0.00000000", CultureInfo.InvariantCulture) + " ₽"
        : "н/д (неизвестная модель или тариф)";
}
