using UniversalAI.Services.Agents;
using UniversalAI.Services;

namespace UniversalAI.ConsoleApp.Commands;

public sealed class AgentCommandHandler : IConsoleCommandHandler
{
    private readonly AgentRegistry _agents;
    private readonly ConversationService _conversations;

    public AgentCommandHandler(
        AgentRegistry agents,
        ConversationService conversations)
    {
        _agents = agents;
        _conversations = conversations;
    }

    public async Task<bool> TryHandleAsync(
        string input,
        ConsoleChatSession session,
        CancellationToken cancellationToken = default)
    {
        string[] parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 ||
            !parts[0].Equals("/agent", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (parts.Length == 1)
        {
            Console.WriteLine($"Текущий агент: {_agents.Get(session.AgentId).DisplayName}");
            Console.WriteLine("Доступные агенты:");
            foreach (IAgent agent in _agents.GetAvailableAgents())
            {
                Console.WriteLine($"  {agent.Id,-8} {agent.DisplayName} — {agent.Description}");
            }

            Console.WriteLine("Выбор: /agent ID");
            return true;
        }

        string agentId = parts[1].Trim();
        try
        {
            IAgent agent = _agents.Get(agentId);
            await _conversations.SetAgentIdAsync(
                session.ConversationId,
                agent.Id,
                cancellationToken);
            session.AgentId = agent.Id;
            Console.WriteLine($"Выбран агент: {agent.DisplayName}");
        }
        catch (InvalidOperationException exception)
        {
            Console.WriteLine(exception.Message);
        }

        return true;
    }
}
