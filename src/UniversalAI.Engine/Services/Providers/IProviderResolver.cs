using UniversalAI.Providers;

namespace UniversalAI.Services.Providers;

public interface IProviderResolver
{
    Task<IAIProvider> GetProviderForConversationAsync(
        int conversationId,
        CancellationToken cancellationToken = default);
}
