using UniversalAI.Data.Entities;
using UniversalAI.Services;

namespace UniversalAI.ConsoleApp.Commands;

public sealed class ConversationCommandHandler : IConsoleCommandHandler
{
    private readonly ConversationService _conversations;

    public ConversationCommandHandler(ConversationService conversations)
    {
        _conversations = conversations;
    }

    public async Task<bool> TryHandleAsync(
        string input,
        ConsoleChatSession session,
        CancellationToken cancellationToken = default)
    {
        if (input.Equals("/history", StringComparison.OrdinalIgnoreCase))
        {
            await ShowHistoryAsync(cancellationToken);
            return true;
        }

        if (input.Equals("/new", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("/new ", StringComparison.OrdinalIgnoreCase))
        {
            await CreateConversationAsync(input, session, cancellationToken);
            return true;
        }

        if (input.Equals("/rename", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("/rename ", StringComparison.OrdinalIgnoreCase))
        {
            await RenameConversationAsync(input, session.ConversationId);
            return true;
        }

        if (input.StartsWith("/open ", StringComparison.OrdinalIgnoreCase))
        {
            await OpenConversationAsync(input, session, cancellationToken);
            return true;
        }

        if (input.Equals("/reset", StringComparison.OrdinalIgnoreCase))
        {
            await _conversations.ClearConversationAsync(
                session.ConversationId,
                cancellationToken);
            Console.WriteLine("История текущего диалога очищена.");
            return true;
        }

        return false;
    }

    private async Task ShowHistoryAsync(CancellationToken cancellationToken)
    {
        List<ConversationEntity> conversations =
            await _conversations.GetConversationsAsync(cancellationToken);

        Console.WriteLine("\n--- История диалогов ---");
        if (conversations.Count == 0)
        {
            Console.WriteLine("Диалогов пока нет.");
            return;
        }

        foreach (ConversationEntity conversation in conversations)
        {
            Console.WriteLine(
                $"ID: {conversation.Id} | {conversation.Title} | " +
                $"{conversation.UpdatedAt.ToLocalTime():dd.MM.yyyy HH:mm}");
        }
    }

    private async Task CreateConversationAsync(
        string input,
        ConsoleChatSession session,
        CancellationToken cancellationToken)
    {
        string title = input.Length > "/new".Length
            ? input["/new".Length..].Trim()
            : "";

        if (string.IsNullOrWhiteSpace(title))
        {
            Console.Write("Название диалога (Enter — по умолчанию): ");
            title = Console.ReadLine()?.Trim() ?? "";
        }

        session.ConversationId =
            await _conversations.CreateConversationAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(title))
        {
            await _conversations.RenameConversationAsync(
                session.ConversationId,
                title);
        }

        ConversationEntity? conversation =
            await _conversations.GetConversationAsync(session.ConversationId);

        Console.WriteLine($"\nСоздан диалог ID: {session.ConversationId}");
        Console.WriteLine($"Название: {conversation?.Title}");
    }

    private async Task RenameConversationAsync(string input, int conversationId)
    {
        string title = input.Length > "/rename".Length
            ? input["/rename".Length..].Trim()
            : "";

        if (string.IsNullOrWhiteSpace(title))
        {
            Console.Write("Новое название диалога: ");
            title = Console.ReadLine()?.Trim() ?? "";
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            Console.WriteLine("Название не может быть пустым.");
            return;
        }

        await _conversations.RenameConversationAsync(conversationId, title);
        Console.WriteLine($"Диалог переименован: {title}");
    }

    private async Task OpenConversationAsync(
        string input,
        ConsoleChatSession session,
        CancellationToken cancellationToken)
    {
        string idText = input["/open ".Length..].Trim();
        if (!int.TryParse(idText, out int requestedId))
        {
            Console.WriteLine("Использование: /open ID");
            return;
        }

        if (!await _conversations.ConversationExistsAsync(requestedId, cancellationToken))
        {
            Console.WriteLine("Диалог с таким ID не найден.");
            return;
        }

        session.ConversationId = requestedId;
        List<Core.ChatMessage> history =
            await _conversations.GetMessagesAsync(requestedId, cancellationToken);
        ConversationEntity? conversation =
            await _conversations.GetConversationAsync(requestedId);

        Console.WriteLine("\nДиалог открыт");
        Console.WriteLine($"ID: {requestedId}");
        Console.WriteLine($"Название: {conversation?.Title ?? "Без названия"}");
        Console.WriteLine($"Загружено сообщений: {history.Count}");
    }
}
