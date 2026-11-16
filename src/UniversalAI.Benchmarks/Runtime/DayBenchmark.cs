namespace UniversalAI.Benchmarks.Runtime;

/// <summary>Typed entry point for an established protocol, independent of CLI argument parsing.</summary>
public sealed class DayBenchmark(BenchmarkDefinition definition,
    Func<BenchmarkSettings, BenchmarkExecutionContext, Task<int>> execute) : IBenchmark
{
    public BenchmarkDefinition Definition => definition;
    public async Task<int> ExecuteAsync(BenchmarkSettings settings, BenchmarkExecutionContext context)
    {
        using var presentation = BenchmarkOutput.Use(context);
        context.CancellationToken.ThrowIfCancellationRequested();
        return await execute(settings, context);
    }
}
