using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UniversalAI.Benchmarks;
using UniversalAI.Benchmarks.Runtime;
using UniversalAI.Configuration;
using UniversalAI.Providers;
using UniversalAI.Services.Providers;
using UniversalAI.Web.Components;

internal static class SnapshotFileChecks
{
    public static async Task RunAsync(string root)
    {
        string folder = Path.Combine(root, "snapshot-locks");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "snapshot.json");
        await SnapshotFile.WriteAllTextAsync(path, "old");
        Task initialReplacement;
        await using (var old = await SnapshotFile.OpenReadAsync(path))
        {
            initialReplacement = SnapshotFile.WriteAllTextAsync(path, "new");
            await Task.Delay(25);
            using var reader = new StreamReader(old, Encoding.UTF8, leaveOpen: true);
            Check(!initialReplacement.IsCompleted && await reader.ReadToEndAsync() == "old",
                "Обновление ждёт закрытия открытого снимка без ошибки и без неполных данных");
        }
        await initialReplacement;
        Check(await SnapshotFile.ReadAllTextAsync(path) == "new", "После закрытия просмотрщика доступен целый новый снимок");
        if (OperatingSystem.IsWindows())
        {
            Task replacement;
            using (var blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                replacement = SnapshotFile.WriteAllTextAsync(path, "after-lock");
                await Task.Delay(75);
                Check(!replacement.IsCompleted, "Воспроизведена Windows-блокировка без FILE_SHARE_DELETE: запись ждёт освобождения");
            }
            await replacement;
            Check(await SnapshotFile.ReadAllTextAsync(path) == "after-lock", "Кратковременная блокировка снята без потери снимка");
            using (var blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var cancellation = new CancellationTokenSource())
            {
                var cancelled = SnapshotFile.WriteAllTextAsync(path, "must-not-publish", cancellation.Token);
                cancellation.Cancel();
                try { await cancelled; throw new Exception("Ожидалась отмена"); }
                catch (OperationCanceledException) { }
            }
            Check(await SnapshotFile.ReadAllTextAsync(path) == "after-lock" && !Directory.GetFiles(folder, "*.tmp").Any(),
                "Отмена повтора сохраняет прежний снимок и убирает временный файл");
            using (var blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool failed = false;
                try { await SnapshotFile.WriteAllTextAsync(path, "still-locked"); }
                catch (IOException e) { failed = e.Message.Contains(path) && e.InnerException != null; }
                Check(failed, "Постоянная блокировка даёт конечную ошибку с точным путём и исходной причиной");
            }
        }

        string? previous = Environment.GetEnvironmentVariable("UNIVERSALAI_DATA_DIR");
        Environment.SetEnvironmentVariable("UNIVERSALAI_DATA_DIR", Path.Combine(root, "snapshot-runs"));
        var config = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json")).Build();
        var services = new ServiceCollection().AddLogging().AddUniversalAI(config).AddUniversalAIBenchmarks();
        Environment.SetEnvironmentVariable("UNIVERSALAI_DATA_DIR", previous);
        var fake = new Day14Checks.InvariantProvider { OnRequest = () => Thread.Sleep(3) };
        services.AddSingleton<IProviderFactory>(new Factory(fake));
        await using var sp = services.BuildServiceProvider();
        var store = sp.GetRequiredService<FileRunStore>();
        var runner = sp.GetRequiredService<BenchmarkRunner>();
        var registry = sp.GetRequiredService<BenchmarkRegistry>();
        var metadata = await runner.CreateAsync("day14", new() { ProviderId = "ollama", Model = "fake-local" });
        var writer = Task.Run(async () =>
        {
            for (int i = 1; i <= 250; i++) await store.SaveAsync(metadata with { CompletedCalls = i });
        });
        var reads = Enumerable.Range(0, 3).Select(async _ =>
        {
            do { _ = (await store.GetAsync(metadata.Id)).CompletedCalls; } while (!writer.IsCompleted);
        }).ToList();
        await Task.WhenAll(reads.Append(writer));
        Check((await store.GetAsync(metadata.Id)).CompletedCalls == 250, "250 атомарных обновлений метаданных с тремя параллельными читателями проходят без Access denied");
        for (int repeat = 0; repeat < 3; repeat++)
        {
            var created = await runner.CreateAsync("day14", new() { ProviderId = "ollama", Model = "fake-local" });
            var executing = Task.Run(() => runner.ExecuteAsync(created));
            int refreshes = 0;
            do
            {
                var run = await store.GetAsync(created.Id);
                var artifacts = await store.ArtifactsAsync(run.Id);
                var view = await ExperimentViewData.LoadAsync(store, run, artifacts, default);
                _ = await ExperimentDocuments.LoadAsync(store, run, registry.Get("day14").Definition, artifacts, view, default);
                refreshes++;
                await Task.Delay(1);
            } while (!executing.IsCompleted);
            var finished = await executing;
            var report = JsonSerializer.Deserialize<Day14InvariantsBenchmark.Report>(await SnapshotFile.ReadAllTextAsync(await store.ArtifactPathAsync(created.Id, "report.json")), FileRunStore.Json)!;
            Check(finished.Status == RunStatus.Completed && finished.CompletedCalls == 10 && report.Completed && report.Observations.Count == 15 && report.Bypasses == 0,
                $"Day14 под непрерывным просмотром: повтор {repeat + 1}/3, обновлений экрана {refreshes}, 15 шагов/10 вызовов/0 обходов");
        }
        Check(fake.Requests.Count == 30, "Повторы файловых операций не повторяют модельные вызовы: ровно 3 × 10");
    }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); Console.WriteLine("OK: " + message); }
    private sealed class Factory(IAIProvider provider) : IProviderFactory { public IAIProvider Create(ProviderProfile profile) => provider; }
}
