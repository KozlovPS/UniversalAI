using UniversalAI.Core;
using UniversalAI.Core.Capabilities;

namespace UniversalAI.Providers;

public interface IAIProvider
{
    string Name { get; }

    ProviderCapabilities Capabilities { get; }

    Task<ChatResult> SendMessageAsync(
        ChatRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> StreamMessageAsync(
        ChatRequest request,
        CancellationToken cancellationToken = default);
}