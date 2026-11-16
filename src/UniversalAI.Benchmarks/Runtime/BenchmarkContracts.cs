using UniversalAI.Services.Providers;

namespace UniversalAI.Benchmarks.Runtime;

public sealed record BenchmarkSettings
{
    public string ProviderId { get; init; } = "proxyapi";
    public string? Model { get; init; }
    public string? HighEffort { get; init; }
    public int Keep { get; init; } = 6;
    public int Batch { get; init; } = 10;
    public bool Generate { get; init; }
    public bool Branches { get; init; } = true;
    public string? TranscriptPath { get; init; }
}

public sealed record BenchmarkDefinition(string Id, string Name, string Description, bool HasModel,
    bool HasReasoning = false, bool HasMemory = false, bool HasBranches = false,
    string ScenarioVersion = "1", string ScorerVersion = "1");

public interface IBenchmark
{
    BenchmarkDefinition Definition { get; }
    Task<int> ExecuteAsync(BenchmarkSettings settings, BenchmarkExecutionContext context);
}

public sealed class BenchmarkRegistry(IEnumerable<IBenchmark> benchmarks)
{
    private readonly Dictionary<string, IBenchmark> _benchmarks = benchmarks.ToDictionary(b => b.Definition.Id, StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<BenchmarkDefinition> List() => _benchmarks.Values.Select(b => b.Definition).ToList();
    public IBenchmark Get(string id) => _benchmarks.GetValueOrDefault(id)
        ?? throw new ArgumentException($"Неизвестный эксперимент '{id}'.");
}

public enum RunStatus { Queued, Running, Completed, Failed, Cancelled, Interrupted }

public sealed record BenchmarkRun
{
    public int SchemaVersion { get; init; } = 1;
    public required string Id { get; init; }
    public required string BenchmarkId { get; init; }
    public required BenchmarkSettings Settings { get; init; }
    public required ProviderProfile Provider { get; init; }
    public required string Model { get; init; }
    public string ApplicationVersion { get; init; } = typeof(BenchmarkRun).Assembly.GetName().Version?.ToString() ?? "unknown";
    public string? SourceRevision { get; init; } = Environment.GetEnvironmentVariable("UNIVERSALAI_SOURCE_REVISION");
    public string ScenarioVersion { get; init; } = "1";
    public string ScorerVersion { get; init; } = "1";
    public string PricingSnapshot { get; init; } = UniversalAI.Configuration.PricingCatalog.Default.Snapshot;
    public RunStatus Status { get; init; } = RunStatus.Queued;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public int CompletedCalls { get; init; }
    public string? Error { get; init; }
    public int? ExitCode { get; init; }
}
