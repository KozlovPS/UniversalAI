using System.Text;
using UniversalAI.Providers;
using UniversalAI.Providers.OpenAICompatible;
using UniversalAI.Core.Capabilities;

namespace UniversalAI.Benchmarks.Runtime;

/// <summary>Per-execution presentation adapter. Never replaces process-wide Console.Out.</summary>
public sealed class BenchmarkExecutionContext
{
    public required string Directory { get; init; }
    public required ProviderProfileSnapshot Profile { get; init; }
    public required Func<IAIProvider> CreateProvider { get; init; }
    public TextWriter Output { get; init; } = TextWriter.Null;
    public bool Interactive { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

public sealed record ProviderProfileSnapshot(string Name, string BaseUrl, string ApiKeyEnvironmentVariable,
    ModelCapabilities? ModelCapabilities = null);

public static class BenchmarkOutput
{
    private static readonly AsyncLocal<BenchmarkExecutionContext?> Slot = new();
    public static BenchmarkExecutionContext? Current => Slot.Value;
    public static IDisposable Use(BenchmarkExecutionContext context)
    {
        var previous = Slot.Value; Slot.Value = context; return new Restore(() => Slot.Value = previous);
    }
    public static void WriteLine(string? text = null) => (Current?.Output ?? Console.Out).WriteLine(text);
    public static void Write(string? text) => (Current?.Output ?? Console.Out).Write(text);
    public static TextWriter Error => Current?.Output ?? Console.Error;
    public static bool IsInputRedirected => Current is { Interactive: false } || Console.IsInputRedirected;
    public static string? ReadLine() { Current?.CancellationToken.ThrowIfCancellationRequested(); return IsInputRedirected ? null : Console.ReadLine(); }

    public static IAIProvider Provider(OpenAICompatibleOptions options, HttpClient http)
    {
        if (Current != null) return Current.CreateProvider();
        var provider = new OpenAICompatibleProvider(http, options);
        provider.ValidateApiKey(); return provider;
    }
    public static ModelCapabilities ResolveModel(string provider, string model) =>
        Current?.Profile.ModelCapabilities ?? ModelCapabilityResolver.Resolve(provider, model);
    public static OpenAICompatibleOptions Options(BenchmarkExecutionContext context) => new()
    {
        Name = context.Profile.Name, BaseUrl = context.Profile.BaseUrl,
        ApiKeyEnvironmentVariable = context.Profile.ApiKeyEnvironmentVariable,
        ModelCapabilities = context.Profile.ModelCapabilities
    };
    private sealed class Restore(Action restore) : IDisposable { public void Dispose() => restore(); }
}
