using UniversalAI.Core;
using UniversalAI.Data.Entities;

namespace UniversalAI.Services;

/// <summary>Storage boundary shared by persistent chats and isolated benchmark snapshots.</summary>
public interface IContextMemoryStore
{
    Task<ContextSummaryEntity?> GetSummaryAsync(int conversationId, CancellationToken ct = default);
    Task SaveSummaryAsync(int conversationId, string content, int coveredMessageCount, ChatResult usage, CancellationToken ct = default);
    Task SaveFactsAsync(ConversationEntity conversation, Dictionary<string, string> facts, CancellationToken ct = default, ChatResult? usage = null);
}
