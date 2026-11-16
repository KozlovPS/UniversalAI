using System.Collections.Concurrent;

namespace UniversalAI.Services;

/// <summary>Serializes a whole turn and settings changes for one conversation in one server.</summary>
public sealed class ConversationGate
{
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();

    public async Task<IDisposable> EnterAsync(int id, CancellationToken cancellationToken = default)
    {
        SemaphoreSlim semaphore = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        return new Lease(semaphore);
    }

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) semaphore.Release(); }
    }
}
