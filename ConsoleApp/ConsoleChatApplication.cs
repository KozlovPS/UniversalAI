using UniversalAI.Services;
using UniversalAI.Services.Agents;
using System.Globalization;
using System.Text;

namespace UniversalAI.ConsoleApp;

public sealed class ConsoleChatApplication(IWorkspaceClient workspace, ConsoleCommandRouter commands)
{
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine("UniversalAI — /help для списка команд");
        var saved = await workspace.ListAsync(cancellationToken);
        var conversation = saved.FirstOrDefault() ?? await workspace.CreateAsync(new(), cancellationToken);
        ConsoleChatSession session = new() { ConversationId = conversation.Id, AgentId = conversation.AgentId };
        Console.WriteLine($"Диалог {conversation.Id}: {conversation.Title}; {conversation.Model}; {conversation.AgentId}");
        while (!session.ShouldExit && !cancellationToken.IsCancellationRequested)
        {
            Console.Write("\n> ");
            string? input = Console.ReadLine();
            if (input == null || input.Equals("/exit", StringComparison.OrdinalIgnoreCase)) break;
            if (string.IsNullOrWhiteSpace(input)) continue;
            if (await commands.TryHandleAsync(input, session, cancellationToken)) continue;
            if (input.Equals("/paste", StringComparison.OrdinalIgnoreCase))
            {
                input = ReadMultilineInput(session);
                if (string.IsNullOrWhiteSpace(input)) continue;
            }
            await SendChatMessageAsync(input, session.ConversationId, cancellationToken);
        }
        Console.WriteLine("Работа завершена.");
    }

    private static string? ReadMultilineInput(ConsoleChatSession session)
    {
        Console.WriteLine("Вставьте текст. /send — отправить, /cancel — отменить.");
        StringBuilder content = new();
        while (true)
        {
            string? line = Console.ReadLine();
            if (line == null) { session.ShouldExit = true; return null; }
            if (line.Equals("/send", StringComparison.OrdinalIgnoreCase)) return content.ToString().TrimEnd('\r', '\n');
            if (line.Equals("/cancel", StringComparison.OrdinalIgnoreCase)) return null;
            content.AppendLine(line);
        }
    }

    private async Task SendChatMessageAsync(string input, int conversationId, CancellationToken cancellationToken)
    {
        try
        {
            AgentTurnResult turn = await workspace.SendAsync(conversationId, input, cancellationToken);
            Console.WriteLine("\nОтвет модели:");
            Console.WriteLine(turn.Response.Content);
            WriteAgentTrace(turn.ModelCalls);
            Console.WriteLine($"\n{turn.ProviderName}; {turn.Model}; finish={turn.Response.FinishReason}; циклов инструментов={turn.ToolRounds}");
            WriteTokenUsage(turn.TokenUsage);
            if (turn.Context is { } context)
            {
                Console.WriteLine($"История: ~{context.RawHistoryTokens} → ~{context.EffectiveHistoryTokens} токенов; исходных сообщений в контексте: {context.RecentMessages}.");
                if (context.SummaryCall is { } summary)
                    Console.WriteLine($"Обновление памяти: вход {summary.PromptTokens}, выход {summary.CompletionTokens} токенов API.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { Console.WriteLine($"Ошибка: {ex.Message}"); }
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
        Console.WriteLine($"Стоимость ответов за ход (без поддержки памяти): {FormatCost(usage.CostRubles)}");
        Console.WriteLine($"Стоимость ответов накопительно (без поддержки памяти): {FormatCost(usage.CumulativeCostRubles)}");
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
