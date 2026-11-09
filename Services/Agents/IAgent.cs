namespace UniversalAI.Services.Agents;

public interface IAgent
{
    string Id { get; }

    string DisplayName { get; }

    string Description { get; }

    Task<AgentTurnResult> RunAsync(
        int conversationId,
        string content,
        CancellationToken cancellationToken = default);
}
