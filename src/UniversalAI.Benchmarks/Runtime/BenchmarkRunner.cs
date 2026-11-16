using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using UniversalAI.Configuration;
using UniversalAI.Core;
using UniversalAI.Core.Capabilities;
using UniversalAI.Providers;
using UniversalAI.Services.Providers;

namespace UniversalAI.Benchmarks.Runtime;

public sealed class BenchmarkRunner(BenchmarkRegistry registry, FileRunStore store,
    ProviderCatalog profiles, IProviderFactory providers)
{
    public async Task<BenchmarkRun> CreateAsync(string benchmarkId, BenchmarkSettings settings, CancellationToken ct = default,
        ProviderProfile? profileOverride = null)
    {
        var benchmark = registry.Get(benchmarkId);
        var profile = profileOverride ?? profiles.Get(settings.ProviderId);
        if (settings.Keep < 2 || settings.Batch < 1) throw new ArgumentException("keep >= 2, batch >= 1.");
        if (profile.Type != "OpenAICompatible") throw new ArgumentException("Эти бенчмарки используют протокол OpenAI-compatible.");
        if (benchmarkId == "day5" && profile.Id != "proxyapi")
            throw new ArgumentException("Day-5 сравнивает зафиксированные облачные модели. Для локальной модели используйте другой эксперимент.");
        if (benchmarkId == "day3" && profile.ModelCapabilities?.SupportsReasoningEffort == false)
            throw new ArgumentException("Day-3 требует модель с поддержкой reasoning_effort.");
        // Freeze the endpoint and settings now, not when the queue eventually executes.
        var frozenProfile = JsonSerializer.Deserialize<ProviderProfile>(JsonSerializer.Serialize(profile))!;
        string model = settings.Model ?? (benchmarkId is "day9" or "day10" && profile.Id == "proxyapi"
            ? "openai/gpt-4o-mini" : profile.DefaultModel);
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Укажите модель.");
        var run = new BenchmarkRun
        {
            Id = Guid.NewGuid().ToString("N"), BenchmarkId = benchmark.Definition.Id,
            Settings = settings with { Model = model }, Provider = frozenProfile, Model = model,
            ScenarioVersion = benchmark.Definition.ScenarioVersion, ScorerVersion = benchmark.Definition.ScorerVersion
        };
        await store.SaveAsync(run, ct);
        return run;
    }

    public async Task<BenchmarkRun> ExecuteAsync(BenchmarkRun run, TextWriter? console = null,
        bool interactive = false, CancellationToken ct = default)
    {
        if (run.Status != RunStatus.Queued) throw new InvalidOperationException("Запуск уже выполнялся.");
        string directory = store.GetArtifactsDirectory(run.Id);
        Directory.CreateDirectory(directory);
        await using var log = new StreamWriter(Path.Combine(store.GetDirectory(run.Id), "output.log"), append: false, new UTF8Encoding(false)) { AutoFlush = true };
        var writer = new TeeWriter(log, console);
        try
        {
            ct.ThrowIfCancellationRequested();
            run = run with { Status = RunStatus.Running, StartedAt = DateTimeOffset.UtcNow };
            await store.SaveAsync(run, ct);
            await File.WriteAllTextAsync(Path.Combine(store.GetDirectory(run.Id), "pricing.json"),
                JsonSerializer.Serialize(PricingCatalog.Default, FileRunStore.Json), ct);
            var recorder = new RecordingProvider(providers.Create(run.Provider), directory, ct, async () =>
            {
                run = run with { CompletedCalls = run.CompletedCalls + 1 };
                await store.SaveAsync(run, CancellationToken.None);
            });
            int code = await registry.Get(run.BenchmarkId).ExecuteAsync(run.Settings, new BenchmarkExecutionContext
            {
                Directory = directory, Profile = new(run.Provider.Name, run.Provider.BaseUrl, run.Provider.ApiKeyEnvironmentVariable ?? "", run.Provider.ModelCapabilities),
                CreateProvider = () => recorder, Output = writer, Interactive = interactive, CancellationToken = ct
            });
            ct.ThrowIfCancellationRequested();
            run = run with { Status = code == 0 ? RunStatus.Completed : RunStatus.Failed, ExitCode = code,
                Error = code == 0 ? null : "Эксперимент завершился с ошибкой. Подробности в журнале и артефактах." };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            run = run with { Status = RunStatus.Cancelled, Error = "Выполнение отменено; частичные результаты сохранены." };
        }
        catch (Exception e)
        {
            run = run with { Status = RunStatus.Failed, Error = e.Message };
            writer.WriteLine(e.Message);
        }
        finally
        {
            run = run with { FinishedAt = DateTimeOffset.UtcNow };
            await store.SaveAsync(run, CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(directory, "pricing.json"), JsonSerializer.Serialize(PricingCatalog.Default, FileRunStore.Json));
        }
        return run;
    }

    private sealed class TeeWriter(TextWriter file, TextWriter? console) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(string? value) { file.Write(value); console?.Write(value); }
        public override void WriteLine(string? value) { file.WriteLine(value); console?.WriteLine(value); }
    }

    private sealed class RecordingProvider(IAIProvider inner, string directory, CancellationToken runToken,
        Func<Task> progress) : IAIProvider
    {
        private int _number;
        public string Name => inner.Name;
        public ProviderCapabilities Capabilities => inner.Capabilities;
        public async Task<ChatResult> SendMessageAsync(ChatRequest request, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(runToken, cancellationToken);
            linked.Token.ThrowIfCancellationRequested();
            int number = ++_number;
            var clock = Stopwatch.StartNew();
            string kind = request.Messages.FirstOrDefault()?.Content is { } prompt &&
                (prompt.StartsWith("Сожми историю", StringComparison.Ordinal) || prompt == UniversalAI.Services.Context.StickyFactsMemory.Instructions)
                ? "memory" : "answer";
            await File.AppendAllTextAsync(Path.Combine(directory, "api-requests.jsonl"),
                JsonSerializer.Serialize(new { number, kind, request }) + "\n", linked.Token);
            ChatResult? response = null; string? error = null;
            try { response = await inner.SendMessageAsync(request, linked.Token); return response; }
            catch (Exception e) { error = e.Message; throw; }
            finally
            {
                await File.AppendAllTextAsync(Path.Combine(directory, "api-calls.jsonl"),
                    JsonSerializer.Serialize(new { number, kind, model = request.Model, response, error, milliseconds = clock.ElapsedMilliseconds }) + "\n");
                await progress();
            }
        }
        public IAsyncEnumerable<string> StreamMessageAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
            inner.StreamMessageAsync(request, cancellationToken);
    }
}

public static class BenchmarkRegistration
{
    public static IServiceCollection AddUniversalAIBenchmarks(this IServiceCollection services)
    {
        services.AddSingleton<IBenchmark>(new DayBenchmark(new("day3", "Промптинг", "Четыре стратегии и два уровня reasoning", true, HasReasoning: true), Day3PromptingBenchmark.ExecuteAsync));
        services.AddSingleton<IBenchmark>(new DayBenchmark(new("day4", "Температура", "Повторяемость и разнообразие ответов", true), Day4TemperatureBenchmark.ExecuteAsync));
        services.AddSingleton<IBenchmark>(new DayBenchmark(new("day5", "Сравнение моделей", "Точность, скорость и стоимость трёх моделей", false), Day5ModelComparisonBenchmark.ExecuteAsync));
        services.AddSingleton<IBenchmark>(new DayBenchmark(new("day9", "Сжатие истории", "Полная история и накопительное summary", true), Day9CompressionBenchmark.ExecuteAsync));
        services.AddSingleton<IBenchmark>(new DayBenchmark(new("day10", "Стратегии памяти", "Окно, facts, summary и независимые ветки", true, HasMemory: true, HasBranches: true), Day10ContextBenchmark.ExecuteAsync));
        services.AddSingleton<BenchmarkRegistry>();
        services.AddSingleton<FileRunStore>();
        services.AddSingleton<BenchmarkRunner>();
        services.AddSingleton<RunMetricsReader>();
        return services;
    }
}
