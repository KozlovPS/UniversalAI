namespace UniversalAI.ConsoleApp.Commands;

public interface IConsoleCommandHandler
{
    Task<bool> TryHandleAsync(
        string input,
        ConsoleChatSession session,
        CancellationToken cancellationToken = default);
}
