namespace UniversalAI.Services.Agents;

public sealed class AgentRegistry
{
    private readonly Dictionary<string, IAgent> _agents;

    public AgentRegistry(
        IEnumerable<IAgent> agents,
        string defaultAgentId = ToolCallingAgent.AgentId)
    {
        _agents = new Dictionary<string, IAgent>(StringComparer.OrdinalIgnoreCase);
        foreach (IAgent agent in agents)
        {
            if (!_agents.TryAdd(agent.Id, agent))
            {
                throw new ArgumentException(
                    $"Агент с ID '{agent.Id}' зарегистрирован больше одного раза.",
                    nameof(agents));
            }
        }

        if (_agents.Count == 0)
        {
            throw new ArgumentException("Нужно зарегистрировать хотя бы одного агента.", nameof(agents));
        }

        if (!_agents.ContainsKey(defaultAgentId))
        {
            throw new ArgumentException(
                $"Агент по умолчанию '{defaultAgentId}' не зарегистрирован.",
                nameof(defaultAgentId));
        }

        DefaultAgentId = defaultAgentId;
    }

    public string DefaultAgentId { get; }

    public IReadOnlyList<IAgent> GetAvailableAgents() =>
        _agents.Values.OrderBy(agent => agent.DisplayName).ToList();

    public IAgent Get(string id)
    {
        return _agents.TryGetValue(id, out IAgent? agent)
            ? agent
            : throw new InvalidOperationException(
                $"Агент '{id}' не найден. Доступные агенты: " +
                string.Join(", ", _agents.Keys.OrderBy(name => name)));
    }
}
