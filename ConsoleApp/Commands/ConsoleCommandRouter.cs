using UniversalAI.ConsoleApp.Commands;

namespace UniversalAI.ConsoleApp;

public sealed class ConsoleCommandRouter
{
    private readonly IReadOnlyList<IConsoleCommandHandler> _handlers;

    public ConsoleCommandRouter(IEnumerable<IConsoleCommandHandler> handlers)
    {
        _handlers = handlers.ToArray();
    }

    public async Task<bool> TryHandleAsync(
        string input,
        ConsoleChatSession session,
        CancellationToken cancellationToken = default)
    {
        foreach (IConsoleCommandHandler handler in _handlers)
        {
            if (await handler.TryHandleAsync(input, session, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }
}
