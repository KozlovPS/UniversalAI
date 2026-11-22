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
        var beforeRun = ExperimentDocuments.Preview(new("day14", "Инварианты", "Проверка инвариантов", true));
        string lessonPreview = WebUtility.HtmlDecode(await Render<ExperimentLesson>(new() { ["Documents"] = beforeRun, ["BenchmarkId"] = "day14" }));
        string scenarioPreview = WebUtility.HtmlDecode(await Render<ExperimentScenario>(new() { ["Documents"] = beforeRun, ["BenchmarkId"] = "day14" }));
        Check(beforeRun.IsPreview && !beforeRun.HasSavedMaterials && beforeRun.Outcomes.Count == 0 && beforeRun.ReportComplete == null &&
            lessonPreview.Contains("Прогон ещё не запущен") && scenarioPreview.Contains(Day14InvariantsBenchmark.Attacks[0]) &&
            scenarioPreview.Contains("только после запуска") && !scenarioPreview.Contains("восстановлены из доступных файлов"),
            "Предпросмотр показывает планируемые задание/сценарий без вымышленных результатов или архивных подписей");
        RenderFragment launch = builder => builder.AddContent(0, "Запустить эксперимент");
        string launchHtml = WebUtility.HtmlDecode(await Render<ExperimentWorkbench>(new() { ["Data"] = data, ["LaunchAction"] = launch }));
        Check(launchHtml.Contains("Запустить эксперимент") && launchHtml.Contains("Управление плавным просмотром"),
            "Кнопка нового запуска доступна в панели рядом с управлением просмотром записи");
        string previewHtml = WebUtility.HtmlDecode(await Render<ExperimentWorkbench>(new() { ["Data"] = data }));
        Check(previewHtml.Contains("Управление плавным просмотром") && !previewHtml.Contains("Сохранённый ответ"),
            "Плавный просмотр не раскрывает ответ до его очереди");
        string html = await Render<ExperimentWorkbench>(new() { ["Data"] = data, ["EnablePlayback"] = false });
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
        CheckPlayback(data.Calls[0]);
        await CheckDocuments(store, run, directory, renderer);
    }
    private static void CheckPlayback(ExperimentViewData.Call original)
    {
        var first = original with { Number = 1, Step = "first" };
        var pending = original with { Number = 2, Step = "second", Kind = "memory", Response = null, Error = null };
        var playback = new ExperimentPlayback();
        playback.Update([first, pending], active: true);
        Check(playback.StageId == "first" && !playback.QuestionVisible(first), "Показ начинается с этапа, без преждевременных реплик");
        playback.Advance();
        Check(playback.QuestionVisible(first) && !playback.AnswerVisible(first) && !playback.ResultVisible(first), "Вопрос, ответ и оценка раскрываются раздельно");
        playback.Pause();
        playback.Advance();
        Check(!playback.Playing && playback.AnswerVisible(first) && !playback.ResultVisible(first), "Ручной шаг сохраняет паузу и показывает только ответ");
        playback.Advance(); playback.Advance();
        Check(playback.StageId == "second" && playback.Phase == ExperimentPlayback.Reveal.Stage && !playback.QuestionVisible(pending), "Переход между этапами оставляет отдельную паузу");
        playback.Advance();
        Check(!playback.Advance() && !playback.AnswerVisible(pending), "Просмотр ждёт настоящий ответ API и не выдумывает завершение");
        var completed = pending with { Response = new() { Content = "Результат обработки памяти" } };
        playback.Update([first, completed], active: false);
        Check(playback.Current?.Number == 2 && playback.Phase == ExperimentPlayback.Reveal.Question, "Polling обновляет данные, сохраняя позицию просмотра");
        playback.Advance(); playback.Advance(); playback.Advance();
        Check(playback.AtEnd && !playback.Playing && playback.ResultVisible(completed), "Завершённая запись останавливается после последнего результата");
        playback.Restart(); playback.SeekStage("second");
        Check(!playback.Playing && playback.StageId == "second" && playback.Phase == ExperimentPlayback.Reveal.Stage, "Выбор этапа ставит воспроизведение на паузу");
        playback.RevealAll();
        Check(playback.ResultVisible(first) && playback.ResultVisible(completed), "Показать всё открывает полную запись без ожидания");
        playback.Play();
        Check(playback.Playing && !playback.ShowingAll && playback.Current?.Number == 1 && first.Response == original.Response,
            "Повтор начинается сначала и не изменяет сохранённые данные");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); Console.WriteLine("OK: " + message); }
    private static async Task CheckDocuments(FileRunStore store, BenchmarkRun run, string directory, HtmlRenderer renderer)
    {
        string markdown = "# Сохранённый отчёт\n\n| Модель | Результат |\n|---|---|\n| **Архивная модель** | 1/2 |\n\n<script>unsafe()</script>\n\n3. Третий пункт";
        await File.WriteAllTextAsync(Path.Combine(directory, "report.md"), markdown);
        await File.WriteAllTextAsync(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(new
        {
            Results = new[] { new { ModelId = "archived-model", CorrectTasks = 1, TaskCount = 2, FailedTasks = 1,
                Cases = new[] { new { TaskId = "archive-task", Question = "Сохранённая задача этого прогона", ExpectedAnswers = new[] { "17" } } } } }
        }));
        run = run with { BenchmarkId = "day5", Status = RunStatus.Failed };
        await store.SaveAsync(run);
        async Task<ExperimentDocuments> Read(BenchmarkDefinition definition)
        {
            var artifacts = await store.ArtifactsAsync(run.Id);
            var view = await ExperimentViewData.LoadAsync(store, run, artifacts, default);
            return await ExperimentDocuments.LoadAsync(store, run, definition, artifacts, view, default);
        }
        var definition = new BenchmarkDefinition("day5", "Сравнение моделей", "Цель методики", false);
        var docs = await Read(definition);
        Check(docs.Materials.Groups.SelectMany(g => g.Items).Any(i => i.Text == "Сохранённая задача этого прогона" && i.Expected == "17") &&
            !docs.Materials.Groups.SelectMany(g => g.Items).Any(i => i.Text.Contains("48 / 6")),
            "Сценарий старого Day-5 берёт задачи из его отчёта, а не из нового набора кода");
        Check(docs.Materials.Assignment == CourseAssignments.Find("day5")!.Text && docs.Outcomes.Single().Passed == 1 && docs.Outcomes.Single().Total == 2,
            "Полный исходник DAYS.md показан без подмены сохранённых оценок");
        async Task<string> Render<T>(Dictionary<string, object?> values) where T : IComponent =>
            WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(values))).ToHtmlString()));
        string lesson = await Render<ExperimentLesson>(new() { ["Documents"] = docs, ["BenchmarkId"] = "day5" });
        Check(lesson.Contains("День 5. Версии моделей") && lesson.Contains("В этом архиве нет отдельного снимка задания"), "Актуальное полное задание для старого архива имеет честную пометку источника");
        string report = await Render<ExperimentReport>(new() { ["Run"] = run, ["Documents"] = docs });
        Check(report.Contains("Частичный отчёт") && !report.Contains("✓ Протокол завершён"), "Незавершённый запуск получает частичный отчёт");
        string safe = SafeReportMarkdown.Render(markdown);
        Check(safe.Contains("<table>") && safe.Contains("<strong>") && safe.Contains("<ol start=\"3\">") &&
            !safe.Contains("<script>") && safe.Contains("&lt;script&gt;"), "Оформленный Markdown сохраняет таблицы и нумерацию, экранируя HTML");
        var frozen = new ExperimentMaterials { Title = "Название из снимка", Goal = "Сохранённая цель", Assignment = "Сохранённое задание", Groups = [new("Набор из снимка", "", [new("Факт", "АРХИВ-777", "17")])] };
        await File.WriteAllTextAsync(Path.Combine(directory, "experiment-materials.json"), JsonSerializer.Serialize(frozen));
        docs = await Read(definition);
        Check(docs.HasSavedMaterials && docs.Materials.Title == "Название из снимка" && docs.Materials.Groups[0].Items[0].Text == "АРХИВ-777",
            "Сохранённый снимок задания и материалов имеет приоритет над текущим каталогом");
        string currentLesson = await Render<ExperimentLesson>(new() { ["Documents"] = docs, ["BenchmarkId"] = "day5" });
        Check(currentLesson.Contains("День 5. Версии моделей") && currentLesson.Contains("полный актуальный текст DAYS.md") &&
            docs.Materials.Assignment == "Сохранённое задание", "Старая запись показывает полный текущий источник с пометкой, исходный snapshot не изменяется");
        await File.WriteAllTextAsync(Path.Combine(directory, "experiment-materials.json"), JsonSerializer.Serialize(frozen with { SchemaVersion = 99 }));
        docs = await Read(definition);
        Check(!docs.HasSavedMaterials && docs.Notices.Count > 0 && docs.Materials.Assignment == CourseAssignments.Find("day5")!.Text,
            "Неизвестная версия материалов оставляет доступ к реальным артефактам");
        run = run with { BenchmarkId = "day11" };
        await store.SaveAsync(run);
        var layers = new Day11MemoryLayersBenchmark.Report(false, "Прервано", "offline", "1", "1", [],
            [new("all", [new() { Role = "user", Content = "Архивный код встречи" }], new() { ["task_ticket"] = "ARCHIVE-777" },
                new() { ["acceptance_owner"] = "Архивный владелец" }, new(10, 10, 10), [])]);
        await File.WriteAllTextAsync(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(layers));
        docs = await Read(new("day11", "Слои памяти", "", true));
        Check(docs.Materials.Groups.SelectMany(g => g.Items).Any(i => i.Text == "ARCHIVE-777") &&
            !docs.Materials.Groups.SelectMany(g => g.Items).Any(i => i.Text == "LAB-118"),
            "Факты старого Day-11 берутся из сохранённого снимка, а не из текущего кода");
    }
    private sealed class NoBrowserJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new InvalidOperationException("SSR must not call JavaScript.");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new InvalidOperationException("SSR must not call JavaScript.");
    }
}
