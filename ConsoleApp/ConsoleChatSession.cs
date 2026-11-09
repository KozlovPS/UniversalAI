using UniversalAI.Services.Agents;

namespace UniversalAI.ConsoleApp;

public sealed class ConsoleChatSession
{
    public int ConversationId { get; set; }

    public string AgentId { get; set; } = ToolCallingAgent.AgentId;

    public bool ShouldExit { get; set; }
}
