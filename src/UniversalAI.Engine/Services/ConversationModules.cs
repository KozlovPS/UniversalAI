using System.Text.Json;
using UniversalAI.Data.Entities;

namespace UniversalAI.Services;

/// <summary>Explicit attachments are independent of stored module data and agent selection.</summary>
public sealed record ConversationModules(bool Tasks = false, bool Personalization = false, bool Invariants = false)
{
    public static ConversationModules Resolve(ConversationEntity conversation)
    {
        // Preserve existing conversations and direct benchmark snapshots until the first explicit selection.
        if (conversation.ModulesJson != "{}")
            return JsonSerializer.Deserialize<ConversationModules>(conversation.ModulesJson)
                ?? throw new InvalidDataException("Настройки возможностей диалога повреждены.");
        return new(conversation.TaskStateJson != "null" || conversation.AgentId == "task",
            Context.UserProfileMemory.Parse(conversation.UserProfileJson).Enabled,
            conversation.InvariantStateJson != "{}" || conversation.AgentId == "invariants");
    }
}
