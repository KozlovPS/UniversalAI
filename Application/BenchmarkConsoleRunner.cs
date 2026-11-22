using Microsoft.Extensions.DependencyInjection;
using UniversalAI.Benchmarks.Runtime;
using UniversalAI.Services.Providers;

namespace UniversalAI.Application;

public static class BenchmarkConsoleRunner
{
    public sealed record Parsed(string BenchmarkId, BenchmarkSettings Settings, string? Output,
        string? BaseUrl, string? ApiKeyEnvironmentVariable, bool Automatic);

    public static Parsed Parse(string[] args, ProviderCatalog? profiles = null)
    {
        string id = args.Length > 0 && !args[0].StartsWith('-') ? args[0].ToLowerInvariant() : "day3";
        int start = args.Length > 0 && !args[0].StartsWith('-') ? 1 : 0;
        BenchmarkSettings settings = new();
        string? output = null, baseUrl = null, key = null; bool auto = false;
        for (int i = start; i < args.Length; i++)
        {
            string option = args[i].ToLowerInvariant();
            if (option == "--auto") { auto = true; continue; }
            if (option == "--generate") { settings = settings with { Generate = true }; continue; }
            if (option == "--skip-branches") { settings = settings with { Branches = false }; continue; }
            if (++i >= args.Length || args[i].StartsWith("--")) throw new ArgumentException($"Нет значения для {option}.");
            string value = args[i];
            switch (option)
            {
                case "--model": settings = settings with { Model = value }; break;
                case "--provider":
                    string profileId = profiles?.Profiles.FirstOrDefault(p => p.Id.Equals(value, StringComparison.OrdinalIgnoreCase) || p.Name.Equals(value, StringComparison.OrdinalIgnoreCase))?.Id ?? value.ToLowerInvariant();
                    settings = settings with { ProviderId = profileId }; break;
                case "--high-effort": settings = settings with { HighEffort = value }; break;
                case "--keep": settings = settings with { Keep = int.Parse(value) }; break;
                case "--batch": settings = settings with { Batch = int.Parse(value) }; break;
                case "--context-window": settings = settings with { ContextWindowTokens = int.Parse(value) }; break;
                case "--transcript": settings = settings with { TranscriptPath = Path.GetFullPath(value) }; break;
                case "--output": output = Path.GetFullPath(value); break;
                case "--base-url": baseUrl = value; break;
                case "--api-key-env": key = value; break;
                default: throw new ArgumentException($"Неизвестный параметр {option}.");
            }
        }
        return new(id, settings, output, baseUrl, key, auto);
    }

    public static async Task<int> RunAsync(IServiceProvider services, string[] args, CancellationToken ct)
    {
        var catalog = services.GetRequiredService<ProviderCatalog>();
        var parsed = Parse(args, catalog);
        ProviderProfile? profile = null;
        if (parsed.BaseUrl != null || parsed.ApiKeyEnvironmentVariable != null)
        {
            var original = catalog.Get(parsed.Settings.ProviderId);
            profile = System.Text.Json.JsonSerializer.Deserialize<ProviderProfile>(System.Text.Json.JsonSerializer.Serialize(original))!;
            if (parsed.BaseUrl != null) { ProviderCatalog.ValidateEndpoint(parsed.BaseUrl); profile.BaseUrl = parsed.BaseUrl; }
            if (parsed.ApiKeyEnvironmentVariable != null) profile.ApiKeyEnvironmentVariable = parsed.ApiKeyEnvironmentVariable;
        }
        if (parsed.Output != null && parsed.BenchmarkId == "day10" && Directory.Exists(parsed.Output) && Directory.EnumerateFileSystemEntries(parsed.Output).Any())
            throw new ArgumentException("Для Day-10 нужен пустой каталог --output.");
        var runner = services.GetRequiredService<BenchmarkRunner>();
        var run = await runner.CreateAsync(parsed.BenchmarkId, parsed.Settings, ct, profile);
        Console.WriteLine($"RunId: {run.Id}");
        run = await runner.ExecuteAsync(run, Console.Out, !parsed.Automatic && !Console.IsInputRedirected, ct);
        var store = services.GetRequiredService<FileRunStore>();
        Console.WriteLine($"{run.Status}; результаты: {store.GetDirectory(run.Id)}");
        if (parsed.Output != null)
        {
            var artifacts = await store.ArtifactsAsync(run.Id, ct);
            if (parsed.BenchmarkId is "day3" or "day4" or "day5")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(parsed.Output)!);
                foreach (string extension in new[] { ".csv", ".json", ".md" })
                {
                    string source = Path.Combine(store.GetArtifactsDirectory(run.Id), "report" + extension);
                    if (File.Exists(source)) File.Copy(source, Path.ChangeExtension(parsed.Output, extension), overwrite: true);
                }
            }
            else
            {
                Directory.CreateDirectory(parsed.Output);
                foreach (string name in artifacts) File.Copy(await store.ArtifactPathAsync(run.Id, name, ct), Path.Combine(parsed.Output, name), overwrite: true);
            }
        }
        return run.Status == RunStatus.Completed ? 0 : run.Status == RunStatus.Cancelled ? 130 : 1;
    }
}
