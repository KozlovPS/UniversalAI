namespace UniversalAI.Benchmarks.Runtime;

/// <summary>
/// Optional, read-only UI evidence saved as experiment-view.json in the run artifacts.
/// Benchmarks keep ownership of execution and scoring. IDs are stable within one run.
/// CallNumbers reference the common api-requests/api-calls recorder, not array positions.
/// Write snapshots atomically; unknown schema versions fall back to the API journal.
/// </summary>
public sealed record ExperimentPresentation
{
    public int SchemaVersion { get; init; } = 1;
    public List<ExperimentStage> Stages { get; init; } = [];
}

public sealed record ExperimentStage
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    public List<int> CallNumbers { get; init; } = [];
    public List<ExperimentChange> Changes { get; init; } = [];
    public List<ExperimentMetric> Metrics { get; init; } = [];
}

// Strings deliberately support facts, state transitions, constraints and non-numeric values.
public sealed record ExperimentChange(string Label, string Before, string After);
public sealed record ExperimentMetric(string Label, string Value, string? Note = null);
