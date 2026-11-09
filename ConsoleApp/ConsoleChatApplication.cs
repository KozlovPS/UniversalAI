using UniversalAI.Data.Entities;
using UniversalAI.Providers;
using UniversalAI.Services;
using UniversalAI.Services.Agents;
using UniversalAI.Services.Providers;

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

        ConsoleChatSession session = new()
        {
            ConversationId = await _conversations.CreateConversationAsync(cancellationToken),
            AgentId = _agents.DefaultAgentId
        };

        ConversationEntity? conversation =
            await _conversations.GetConversationAsync(session.ConversationId);
        IAIProvider provider =
            await _providers.GetProviderForConversationAsync(session.ConversationId, cancellationToken);

        Console.WriteLine($"Провайдер: {provider.Name}");
        Console.WriteLine($"Модель: {conversation?.Model}");
        Console.WriteLine($"Агент: {_agents.Get(session.AgentId).DisplayName}");
        Console.Write("Название нового диалога (Enter — по умолчанию): ");

        string? title = Console.ReadLine()?.Trim();
        if (!string.IsNullOrWhiteSpace(title))
        {
            await _conversations.RenameConversationAsync(session.ConversationId, title);
        }

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

            await SendChatMessageAsync(
                input,
                session.ConversationId,
                _agents.Get(session.AgentId),
                cancellationToken);
        }

        Console.WriteLine("\nРабота завершена.");
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
            Console.WriteLine($"Prompt tokens: {turn.Response.PromptTokens}");
            Console.WriteLine($"Completion tokens: {turn.Response.CompletionTokens}");
            Console.WriteLine($"Total tokens: {turn.Response.TotalTokens}");
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
                $"(вход {modelCall.PromptTokens}, выход {modelCall.CompletionTokens})");

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
