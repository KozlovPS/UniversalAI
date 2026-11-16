using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace UniversalAI.Benchmarks.Runtime;

/// <summary>One worker preserves the sequential benchmark methodology and bounds API spending.</summary>
public sealed class BenchmarkQueue(BenchmarkRunner runner, FileRunStore store, ILogger<BenchmarkQueue> logger) : BackgroundService
{
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(32)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.Wait
    });
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new();
    private readonly SemaphoreSlim _submission = new(1, 1);

    public async Task<BenchmarkRun> SubmitAsync(string id, BenchmarkSettings settings, CancellationToken ct = default)
    {
        await _submission.WaitAsync(ct);
        try
        {
            if (_queue.Reader.Count >= 32) throw new InvalidOperationException("Очередь заполнена.");
            var run = await runner.CreateAsync(id, settings, ct);
            if (!_queue.Writer.TryWrite(run.Id))
            {
                await store.SaveAsync(run with { Status = RunStatus.Interrupted, Error = "Не удалось поставить в очередь." }, CancellationToken.None);
                throw new InvalidOperationException("Очередь недоступна.");
            }
            return run;
        }
        finally { _submission.Release(); }
    }

    public async Task CancelAsync(string id, CancellationToken ct = default)
    {
        await _submission.WaitAsync(ct);
        try
        {
            var run = await store.GetAsync(id, ct);
            if (_cancellations.TryGetValue(id, out var cts)) { await cts.CancelAsync(); return; }
            if (run.Status == RunStatus.Queued)
                await store.SaveAsync(run with { Status = RunStatus.Cancelled, FinishedAt = DateTimeOffset.UtcNow }, ct);
        }
        finally { _submission.Release(); }
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // No automatic retry of paid requests: a restart leaves an inspectable interrupted run.
        await store.RecoverInterruptedAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            try
            {
                BenchmarkRun run;
                await _submission.WaitAsync(stoppingToken);
                try
                {
                    run = await store.GetAsync(id, stoppingToken);
                    if (run.Status != RunStatus.Queued) continue;
                    _cancellations[id] = cancellation;
                }
                finally { _submission.Release(); }
                await runner.ExecuteAsync(run, ct: cancellation.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception e) { logger.LogError(e, "Benchmark {RunId} failed", id); }
            finally { _cancellations.TryRemove(id, out _); }
        }
    }
}
