using UniversalAI.Core;
using UniversalAI.Services.Agents;

namespace UniversalAI.Services;

public sealed record ConversationInfo(int Id, string Title, string Model, string AgentId,
    string? ProviderProfileId, string MemoryStrategy, bool CompressionEnabled,
    int Keep, int Batch, double Temperature, int? MaxTokens, string SystemPrompt,
    DateTime UpdatedAt, string FactsJson, string? Summary, double? TopP = null,
    double? FrequencyPenalty = null, double? PresencePenalty = null, int? Seed = null,
    string? ReasoningEffort = null, int? ReasoningBudgetTokens = null, string WorkingMemoryJson = "{}",
    string UserProfileJson = "{}", Tasks.TaskWorkflowState? TaskState = null);
public sealed record CreateConversationRequest(string? Title = null, string? ProviderId = null,
    string? Model = null, string AgentId = "direct");
public sealed record ConversationSettings(string? Model = null, string? AgentId = null,
    string? ProviderId = null, string? MemoryStrategy = null, bool? CompressionEnabled = null,
    int? Keep = null, int? Batch = null, double? Temperature = null, int? MaxTokens = null,
    string? SystemPrompt = null, string? Title = null, double? TopP = null,
    double? FrequencyPenalty = null, double? PresencePenalty = null, int? Seed = null,
    string? ReasoningEffort = null, int? ReasoningBudgetTokens = null,
    bool ClearReasoning = false, bool ResetGeneration = false,
    string? WorkingMemoryJson = null, string? LongTermMemoryJson = null, bool ClearShortTerm = false,
    string? UserProfileJson = null, bool ClearMaxTokens = false);
public sealed record SendMessageRequest(string Content);
public sealed record MessagePage(IReadOnlyList<ChatMessage> Items, int Total, int Offset);
public sealed record AgentInfo(string Id, string Name, string Description);
public sealed record ProviderInfo(string Id, string Name, string Type, string BaseUrl,
    string DefaultModel, bool SupportsTools, bool KeyConfigured);
public sealed record TokenStatistics(ConversationTokenUsage Answers, int MemoryPromptTokens,
    int MemoryCompletionTokens, bool MemoryUsageComplete);

public interface IWorkspaceClient
{
    Task<IReadOnlyList<ProviderInfo>> ProvidersAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AgentInfo>> AgentsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> ModelsAsync(int id, CancellationToken ct = default);
    Task<TokenStatistics> TokensAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<ConversationInfo>> ListAsync(CancellationToken ct = default);
    Task<ConversationInfo> CreateAsync(CreateConversationRequest request, CancellationToken ct = default);
    Task<ConversationInfo> GetAsync(int id, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    Task<MessagePage> MessagesAsync(int id, int offset = 0, int limit = 100, CancellationToken ct = default);
    Task<ConversationInfo> UpdateAsync(int id, ConversationSettings settings, CancellationToken ct = default);
    Task ResetAsync(int id, CancellationToken ct = default);
    Task<AgentTurnResult> SendAsync(int id, string content, CancellationToken ct = default);
    Task<ConversationInfo> CompressAsync(int id, CancellationToken ct = default);
    Task<ConversationInfo> BranchAsync(int id, string title, CancellationToken ct = default);
}
