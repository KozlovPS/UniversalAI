using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using UniversalAI.Benchmarks;
using UniversalAI.Benchmarks.Runtime;
using UniversalAI.Configuration;
using UniversalAI.Core;
using UniversalAI.Services.Providers;
using UniversalAI.Web.Components;

static class ExperimentPresentationChecks
{
    public static async Task RunAsync(string root)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Storage:RootPath"] = Path.Combine(root, "presentation") }).Build();
        // AppPaths uses the same configuration contract as production.
        string? previousRoot = Environment.GetEnvironmentVariable("UNIVERSALAI_DATA_DIR");
        Environment.SetEnvironmentVariable("UNIVERSALAI_DATA_DIR", Path.Combine(root, "presentation"));
        var store = new FileRunStore(new AppPaths(config));
        Environment.SetEnvironmentVariable("UNIVERSALAI_DATA_DIR", previousRoot);
        var run = new BenchmarkRun { Id = Guid.NewGuid().ToString("N"), BenchmarkId = "future-fixture",
            Model = "offline", Provider = new ProviderProfile { Id = "fixture", Name = "Fixture", Type = "OpenAICompatible" },
            Settings = new(), Status = RunStatus.Interrupted };
        await store.SaveAsync(run);
        string directory = store.GetArtifactsDirectory(run.Id);
        Directory.CreateDirectory(directory);
        var request = new ChatRequest { Model = "offline", Messages = [new() { Role = "user", Content = "<script>unsafe()</script>" }] };
        await File.WriteAllTextAsync(Path.Combine(directory, "api-requests.jsonl"), JsonSerializer.Serialize(new { number = 1, kind = "answer", request }) + "\n{\"number\":2");
        await File.WriteAllTextAsync(Path.Combine(directory, "api-calls.jsonl"), JsonSerializer.Serialize(new { number = 1, response = new ChatResult { Content = "Сохранённый ответ" }, error = (string?)null, milliseconds = 120 }) + "\n");
        async Task<ExperimentViewData> Load() => await ExperimentViewData.LoadAsync(store, run, await store.ArtifactsAsync(run.Id), default);
        var data = await Load();
        Check(data.Calls.Count == 1 && data.Calls[0].Response?.Content == "Сохранённый ответ" && data.Notices.Count > 0,
            "Неполная JSONL-запись не скрывает уже полученные ответы");

        var presentation = new ExperimentPresentation { Stages = [new() { Id = "validation", Title = "Проверка ограничения",
            CallNumbers = [1], Changes = [new("Решение", "Ожидает", "Проверено")], Metrics = [new("Проверки", "1 / 1", "Синтетические данные теста")] },
            new() { Id = "done", Title = "Следующий этап ещё не начат" }] };
        string manifestPath = Path.Combine(directory, "experiment-view.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(presentation));
        data = await Load();
        Check(data.Steps.Count == 2 && data.Calls[0].Step == "validation", "Незнакомый эксперимент задаёт этапы и переходы без правок оболочки");

        await using var services = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime, NoBrowserJs>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        async Task<string> Render<T>(Dictionary<string, object?> parameters) where T : IComponent =>
            await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());
        string html = await Render<ExperimentWorkbench>(new() { ["Data"] = data });
        Check(!html.Contains("<script>unsafe()") && html.Contains("&lt;script&gt;") && WebUtility.HtmlDecode(html).Contains("Сохранённый ответ"),
            "Диалог безопасно отображает исходный текст и реальные ответы");
        string inspector = WebUtility.HtmlDecode(await Render<ExperimentContext>(new() { ["Data"] = data, ["Step"] = data.Steps[0], ["Call"] = data.Calls[0] }));
        Check(inspector.Contains("Ожидает") && inspector.Contains("Проверено") && inspector.Contains("1 / 1"), "Инспектор показывает произвольные изменения и метрики будущего урока");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(presentation with { SchemaVersion = 99 }));
        data = await Load();
        Check(data.Presentation == null && data.Calls.Count == 1, "Неизвестная версия представления сохраняет базовый просмотр API");

        run = run with { BenchmarkId = "day10" };
        await store.SaveAsync(run);
        List<ChatMessage> history = Enumerable.Range(1, 10).Select(i => new ChatMessage { Role = i % 2 == 1 ? "user" : "assistant", Content = i % 2 == 1 ? $"Реплика-{i}" : "Принято." }).ToList();
        var call = new Day10Call { Number = 1, Strategy = "SlidingWindow", Branch = "base", Scope = "comparison", Kind = "answer",
            Request = new() { Messages = [new() { Role = "system", Content = "Правило" }, .. history.TakeLast(4), new() { Role = "user", Content = "Вопрос" }] }, Response = new() { Content = "Ответ" } };
        await File.WriteAllTextAsync(Path.Combine(directory, "requests.jsonl"), JsonSerializer.Serialize(call) + "\n");
        await File.WriteAllTextAsync(Path.Combine(directory, "calls.jsonl"), JsonSerializer.Serialize(call) + "\n");
        await File.WriteAllTextAsync(Path.Combine(directory, "SlidingWindow-checkpoint.json"), JsonSerializer.Serialize(new Day10MemoryState { History = history }));
        data = await Load();
        inspector = WebUtility.HtmlDecode(await Render<ExperimentContext>(new() { ["Data"] = data, ["Step"] = data.Steps[0], ["Call"] = data.Calls[0] }));
        Check(inspector.Contains("Вне контекста · 6") && inspector.Contains("Реплика-1") && inspector.Contains("Реплика-9") && inspector.Contains("2: вне контекста") && inspector.Contains("8: в запросе") && history.Count == 10,
            "SlidingWindow показывает 10 → 4 и все исключённые реплики, сохраняя архив");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); Console.WriteLine("OK: " + message); }
    private sealed class NoBrowserJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new InvalidOperationException("SSR must not call JavaScript.");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new InvalidOperationException("SSR must not call JavaScript.");
    }
}
