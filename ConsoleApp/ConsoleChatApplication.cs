using UniversalAI.Data.Entities;
using UniversalAI.Providers;
using UniversalAI.Services;
using UniversalAI.Services.Agents;
using UniversalAI.Services.Providers;
using System.Globalization;
using System.Text;

namespace UniversalAI.ConsoleApp;

public sealed class ConsoleChatApplication
{
    private readonly ConversationService _conversations;
    private readonly ProviderManager _providers;
    private readonly AgentRegistry _agents;
    private readonly ConsoleCommandRouter _commands;

    public ConsoleChatApplication(
        ConversationService conversations,
        ProviderManager providers,
        AgentRegistry agents,
        ConsoleCommandRouter commands)
    {
        _conversations = conversations;
        _providers = providers;
        _agents = agents;
        _commands = commands;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine("================================");
        Console.WriteLine("       UniversalAI");
        Console.WriteLine("================================");

        List<ConversationEntity> savedConversations =
            await _conversations.GetConversationsAsync(cancellationToken);
        ConsoleChatSession session;

        if (savedConversations.Count > 0)
        {
            ConversationEntity latestConversation = savedConversations[0];
            session = new ConsoleChatSession
            {
                ConversationId = latestConversation.Id,
                AgentId = latestConversation.AgentId
            };

            List<UniversalAI.Core.ChatMessage> history =
                await _conversations.GetMessagesAsync(
                    latestConversation.Id,
                    cancellationToken);
            Console.WriteLine(
                $"Продолжаем диалог ID {latestConversation.Id}: " +
                $"{latestConversation.Title} ({history.Count} сообщений).");
            Console.WriteLine("Чтобы начать новый диалог, введите /new.");
        }
        else
        {
            session = new ConsoleChatSession
            {
                ConversationId = await _conversations.CreateConversationAsync(cancellationToken),
                AgentId = _agents.DefaultAgentId
            };

            Console.Write("Название нового диалога (Enter — по умолчанию): ");
            string? title = Console.ReadLine()?.Trim();
            if (!string.IsNullOrWhiteSpace(title))
            {
                await _conversations.RenameConversationAsync(session.ConversationId, title);
            }
        }

        ConversationEntity? conversation =
            await _conversations.GetConversationAsync(session.ConversationId);
        IAIProvider provider =
            await _providers.GetProviderForConversationAsync(session.ConversationId, cancellationToken);

        Console.WriteLine($"Провайдер: {provider.Name}");
        Console.WriteLine($"Модель: {conversation?.Model}");
        Console.WriteLine($"Агент: {_agents.Get(session.AgentId).DisplayName}");

        Console.WriteLine($"\nID диалога: {session.ConversationId}");
        Console.WriteLine("\nДля просмотра списка команд введите /help.");

        while (!session.ShouldExit && !cancellationToken.IsCancellationRequested)
        {
            Console.Write("\n> ");
            string? input = Console.ReadLine();
            if (input == null)
            {
                session.ShouldExit = true;
                continue;
            }

            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }

            if (input.Equals("/exit", StringComparison.OrdinalIgnoreCase))
            {
                session.ShouldExit = true;
                continue;
            }

            if (await _commands.TryHandleAsync(input, session, cancellationToken))
            {
                continue;
            }

            if (input.Equals("/paste", StringComparison.OrdinalIgnoreCase))
            {
                string? pastedText = ReadMultilineInput(session);
                if (pastedText == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(pastedText))
                {
                    Console.WriteLine("Вставка пустая, запрос не отправлен.");
                    continue;
                }

                input = pastedText;
            }

            await SendChatMessageAsync(
                input,
                session.ConversationId,
                _agents.Get(session.AgentId),
                cancellationToken);
        }

        Console.WriteLine("\nРабота завершена.");
    }

    private static string? ReadMultilineInput(ConsoleChatSession session)
    {
        Console.WriteLine(
            "Вставьте текст. Завершите ввод отдельной строкой /send; " +
            "для отмены введите /cancel.");

        StringBuilder content = new();
        while (true)
        {
            string? line = Console.ReadLine();
            if (line == null)
            {
                session.ShouldExit = true;
                return null;
            }

            if (line.Equals("/send", StringComparison.OrdinalIgnoreCase))
            {
                string pastedText = content.ToString().TrimEnd('\r', '\n');
                int estimatedTokens = TokenUsageCalculator.EstimateText(pastedText);
                Console.WriteLine(
                    $"Собрано {pastedText.Length:N0} символов; " +
                    $"оценка текста — около {estimatedTokens:N0} токенов.");
                return pastedText;
            }

            if (line.Equals("/cancel", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Вставка отменена.");
                return null;
            }

            content.AppendLine(line);
        }
    }

    private async Task SendChatMessageAsync(
        string input,
        int conversationId,
        IAgent agent,
        CancellationToken cancellationToken)
    {
        try
        {
            AgentTurnResult turn = await agent.RunAsync(
                conversationId,
                input,
                cancellationToken);

            Console.WriteLine("\nОтвет модели:");
            Console.WriteLine(turn.Response.Content);
            WriteAgentTrace(turn.ModelCalls);
            Console.WriteLine("\n--- Статистика ---");
            Console.WriteLine($"Агент: {agent.DisplayName}");
            Console.WriteLine($"Провайдер: {turn.ProviderName}");
            Console.WriteLine($"Модель: {turn.Model}");
            Console.WriteLine($"Finish reason: {turn.Response.FinishReason}");
            Console.WriteLine($"Циклы инструментов: {turn.ToolRounds}");
            WriteTokenUsage(turn.TokenUsage);
        }
        catch (ModelContextLimitException ex)
        {
            Console.WriteLine("\nМодель отклонила запрос: превышен лимит контекста.");
            Console.WriteLine(ex.Message);
            if (!string.IsNullOrWhiteSpace(ex.InnerException?.Message))
            {
                Console.WriteLine($"Ответ API: {FormatTraceValue(ex.InnerException.Message)}");
            }

            Console.WriteLine(
                "Ответ не получен, ход диалога не сохранён. " +
                "Можно очистить историю командой /reset или сократить запрос.");
        }
        catch (NotSupportedException ex)
        {
            Console.WriteLine("\nНастройка не поддерживается:");
            Console.WriteLine(ex.Message);
        }
        catch (Exception ex)
        {
            Console.WriteLine("\nОшибка при обращении к AI:");
            Console.WriteLine(ex.Message);
            Console.WriteLine("Сообщение не сохранено.");
        }
    }

    private static void WriteTokenUsage(AgentTokenUsage usage)
    {
        Console.WriteLine("\n--- Токены и стоимость ---");
        Console.WriteLine($"Текущий запрос (оценка): {usage.CurrentRequestEstimate}");
        Console.WriteLine($"История до запроса (оценка): {usage.HistoryEstimateBefore}");
        Console.WriteLine($"Полный первый контекст (оценка): {usage.EstimatedPrompt}");
        Console.WriteLine($"История после ответа (оценка): {usage.HistoryEstimateAfter}");
        Console.WriteLine(
            $"Usage API за ход: prompt {FormatCount(usage.PromptTokens)}, " +
            $"completion {FormatCount(usage.CompletionTokens)}, " +
            $"total {FormatCount(usage.TotalTokens)}");
        Console.WriteLine(
            $"Накопительно по сохранённым usage ({usage.MeasuredHistoryTurns} прошлых ходов): " +
            $"prompt {FormatCount(usage.CumulativePromptTokens)}, " +
            $"completion {FormatCount(usage.CumulativeCompletionTokens)}");
        Console.WriteLine($"Стоимость за ход: {FormatCost(usage.CostRubles)}");
        Console.WriteLine($"Стоимость накопительно: {FormatCost(usage.CumulativeCostRubles)}");
        Console.WriteLine(
            "Оценка контекста приблизительная; usage API — фактические токены, " +
            "сообщённые провайдером.");
    }

    private static string FormatCount(int? value) => value?.ToString() ?? "н/д";

    private static string FormatCost(decimal? value) => value.HasValue
        ? value.Value.ToString("0.00000000", CultureInfo.InvariantCulture) + " ₽"
        : "н/д (тариф не задан или API не вернул usage)";

    private static void WriteAgentTrace(
        IReadOnlyList<AgentModelCallTrace> modelCalls)
    {
        Console.WriteLine("\n--- Ходы агента ---");
        foreach (AgentModelCallTrace modelCall in modelCalls)
        {
            Console.WriteLine(
                $"Запрос к модели #{modelCall.Number}: " +
                $"finish={modelCall.FinishReason}, " +
                $"токены={modelCall.TotalTokens} " +
                $"(вход {modelCall.PromptTokens}, выход {modelCall.CompletionTokens}), " +
                $"оценка контекста={modelCall.EstimatedPromptTokens}");

            if (modelCall.ToolCalls.Count == 0)
            {
                Console.WriteLine("  Модель сформировала финальный ответ.");
                continue;
            }

            foreach (AgentToolCallTrace toolCall in modelCall.ToolCalls)
            {
                Console.WriteLine(
                    $"  Инструмент: {toolCall.Name}({FormatTraceValue(toolCall.Arguments)})");
                Console.WriteLine($"  Результат: {FormatTraceValue(toolCall.Result)}");
            }
        }
    }

    private static string FormatTraceValue(string value)
    {
        string singleLine = value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        const int maximumLength = 500;
        return singleLine.Length <= maximumLength
            ? singleLine
            : singleLine[..maximumLength] + "…";
    }
}
