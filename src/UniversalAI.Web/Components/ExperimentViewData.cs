using System.Text.Json;
using UniversalAI.Benchmarks;
using UniversalAI.Benchmarks.Runtime;
using UniversalAI.Core;

namespace UniversalAI.Web.Components;

/// <summary>Read-only presentation of recorded evidence. Never rebuilds or changes benchmark memory.</summary>
public sealed class ExperimentViewData
{
    public sealed record Call(int Number, string Step, string Kind, ChatRequest Request,
        ChatResult? Response, string? Error, double? Seconds, string? Expected = null, bool? Passed = null);
    public sealed record Step(string Id, string Title, string Description);
    public List<Call> Calls { get; } = [];
    public List<Step> Steps { get; } = [];
    public Dictionary<string, Day10MemoryState> Checkpoints { get; } = [];
    public Day11MemoryLayersBenchmark.Report? Layers { get; private set; }
    public Day12PersonalizationBenchmark.Report? Personalization { get; private set; }
    public Day13TaskStateBenchmark.Report? TaskWorkflow { get; private set; }
    public Day14InvariantsBenchmark.Report? Invariants { get; private set; }
    public Day15MemoryUpdatesBenchmark.Report? MemoryUpdates { get; private set; }
    public FoundationBenchmarks.Report? Foundation { get; private set; }
    public ExperimentPresentation? Presentation { get; private set; }
    public List<string> Notices { get; } = [];
    private static readonly JsonSerializerOptions Json = new(FileRunStore.Json);

    public static string LayerStageName(string stage) => stage switch
    {
        "all" => "Все слои заполнены", "clear-short" => "Очистка диалога",
        "clear-working" => "Очистка рабочей памяти", "clear-long" => "Очистка facts", _ => stage
    };

    public static async Task<ExperimentViewData> LoadAsync(FileRunStore store, BenchmarkRun run,
        IReadOnlyList<string> artifacts, CancellationToken ct, Day11MemoryLayersBenchmark.Report? previousLayers = null)
    {
        var view = new ExperimentViewData();
        async Task<T?> Read<T>(string name)
        {
            if (!artifacts.Contains(name)) return default;
            try { return JsonSerializer.Deserialize<T>(await SnapshotFile.ReadAllTextAsync(await store.ArtifactPathAsync(run.Id, name), ct), Json); }
            catch (Exception e) when (e is JsonException or IOException)
            { view.Notices.Add($"{name}: снимок временно недоступен. Откройте исходный журнал или обновите экран."); return default; }
        }
        async Task<List<T>> Lines<T>(string name)
        {
            List<T> rows = [];
            if (!artifacts.Contains(name)) return rows;
            try
            {
                using var file = new FileStream(await store.ArtifactPathAsync(run.Id, name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(file);
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try { if (JsonSerializer.Deserialize<T>(line, Json) is { } row) rows.Add(row); }
                    catch (JsonException) { view.Notices.Add($"{name}: неполная запись; показаны доступные данные."); }
                }
            }
            catch (IOException) { view.Notices.Add($"{name}: файл обновляется. Повторите чтение."); }
            return rows;
        }

        if (run.BenchmarkId == "day11")
            view.Layers = await Read<Day11MemoryLayersBenchmark.Report>("report.json") ?? previousLayers;
        if (run.BenchmarkId == "day12")
            view.Personalization = await Read<Day12PersonalizationBenchmark.Report>("report.json");
        if (run.BenchmarkId == "day13")
            view.TaskWorkflow = await Read<Day13TaskStateBenchmark.Report>("report.json");
        if (run.BenchmarkId == "day14")
            view.Invariants = await Read<Day14InvariantsBenchmark.Report>("report.json");
        if (run.BenchmarkId == "day15")
            view.MemoryUpdates = await Read<Day15MemoryUpdatesBenchmark.Report>("report.json");
        if (run.BenchmarkId is "day1" or "day2" or "day6" or "day7" or "day8")
            view.Foundation = await Read<FoundationBenchmarks.Report>("report.json");

        if (run.BenchmarkId == "day10")
        {
            // Day-10 adds strategy/branch metadata that the common API recorder does not have.
            var requests = await Lines<Day10Call>("requests.jsonl");
            var responses = (await Lines<Day10Call>("calls.jsonl")).ToDictionary(c => c.Number);
            var assessments = new Dictionary<string, List<Day10Answer>>();
            foreach (var strategy in new[] { "SlidingWindow", "StickyFacts", "Summary" })
            {
                if (await Read<Day10MemoryState>($"{strategy}-checkpoint.json") is { } state)
                    view.Checkpoints[strategy] = state;
                if (await Read<List<Day10Answer>>($"{strategy}-answers.json") is { } answers)
                    assessments[strategy] = answers;
            }
            foreach (var request in requests)
            {
                responses.TryGetValue(request.Number, out var response);
                var id = $"{request.Strategy}/{request.Branch}/{request.Scope}";
                if (view.Steps.All(s => s.Id != id))
                    view.Steps.Add(new(id, request.Strategy,
                        request.Scope == "preparation" ? "Подготовка общего диалога" : request.Branch == "base" ? "Основной диалог · проверка памяти" : $"Независимая ветка {request.Branch}"));
                var assessment = request.Branch == "base" && request.Scope == "comparison" && request.Kind == "answer"
                    ? assessments.GetValueOrDefault(request.Strategy)?.FirstOrDefault(a => a.Question == request.Request.Messages.LastOrDefault()?.Content && a.Answer == response?.Response?.Content) : null;
                view.Calls.Add(new(request.Number, id, request.Kind, request.Request, response?.Response,
                    response?.Error, response?.Seconds, assessment?.Expected, assessment?.Correct));
            }
        }
        else
        {
            var requests = await Lines<ApiRequest>("api-requests.jsonl");
            var responses = (await Lines<ApiResponse>("api-calls.jsonl")).ToDictionary(c => c.Number);
            foreach (var request in requests)
            {
                responses.TryGetValue(request.Number, out var response);
                var observation = view.Layers?.Observations.ElementAtOrDefault(request.Number - 1);
                var preference = view.Personalization?.Observations.FirstOrDefault(o => o.CallNumber == request.Number);
                var task = view.TaskWorkflow?.Observations.FirstOrDefault(o => o.CallNumbers.Contains(request.Number));
                var invariant = view.Invariants?.Observations.Where(o => o.Turn.Request != null).ElementAtOrDefault(request.Number - 1);
                var memoryUpdate = view.MemoryUpdates?.Observations.FirstOrDefault(o => o.CallNumber == request.Number);
                var foundation = view.Foundation?.Observations.FirstOrDefault(o => o.CallNumbers.Contains(request.Number));
                // Day-11 has exactly six independent probes per saved stage; no memory API calls.
                var stage = run.BenchmarkId == "day11"
                    ? view.Layers?.Snapshots.ElementAtOrDefault((request.Number - 1) / 6)?.Stage ?? "execution"
                    : "execution";
                if (view.Steps.All(s => s.Id != stage)) view.Steps.Add(new(stage,
                    stage == "execution" ? "Выполнение эксперимента" : LayerStageName(stage),
                    run.BenchmarkId == "day11" ? "Шесть независимых проверок" : "Записанные обращения к модели"));
                view.Calls.Add(new(request.Number, stage, request.Kind, request.Request, response?.Response,
                    response?.Error, response?.Milliseconds / 1000.0, observation?.Expected ?? (preference == null ? null :
                        $"Объём: {preference.ExpectedProfile.Length}; код: {preference.ExpectedProfile.Code}; тон: {preference.ExpectedProfile.Tone}") ??
                        (task == null ? null : $"Разрешено={task.ExpectedAllowed}; этап={task.ExpectedStage}; пауза={task.ExpectedPaused}") ?? invariant?.Expected ?? memoryUpdate?.Expected ?? foundation?.Expected,
                        observation?.Passed ?? preference?.Assessment.Passed ?? task?.Passed ?? invariant?.Passed ?? memoryUpdate?.Passed ?? foundation?.Passed));
            }
            if (view.Layers != null)
                foreach (var snapshot in view.Layers.Snapshots)
                    if (view.Steps.All(s => s.Id != snapshot.Stage))
                        view.Steps.Add(new(snapshot.Stage, LayerStageName(snapshot.Stage), "Этап сохранён · ожидание ответов"));
        }
        // Future experiments can describe their own stages, measurements and before/after
        // evidence without teaching the shell about another day or changing the runner.
        var presentation = await Read<ExperimentPresentation>("experiment-view.json");
        if (presentation != null)
        {
            if (presentation.SchemaVersion != 1 || presentation.Stages == null ||
                presentation.Stages.Any(s => s == null || s.Id == "unassigned" || string.IsNullOrWhiteSpace(s.Id) || string.IsNullOrWhiteSpace(s.Title) || s.CallNumbers == null || s.Changes == null || s.Metrics == null || s.Changes.Any(c => c == null) || s.Metrics.Any(m => m == null)) ||
                presentation.Stages.Select(s => s.Id).Distinct().Count() != presentation.Stages.Count ||
                presentation.Stages.SelectMany(s => s.CallNumbers).Distinct().Count() != presentation.Stages.Sum(s => s.CallNumbers.Count))
                view.Notices.Add("experiment-view.json: версия или структура не поддерживается. Показан журнал API.");
            else
            {
                view.Presentation = presentation;
                view.Steps.Clear();
                view.Steps.AddRange(presentation.Stages.Select(s => new Step(s.Id, s.Title, s.Description)));
                var assignment = presentation.Stages.SelectMany(s => s.CallNumbers.Select(n => (Number: n, Stage: s.Id))).ToDictionary(p => p.Number, p => p.Stage);
                for (int i = 0; i < view.Calls.Count; i++)
                    view.Calls[i] = view.Calls[i] with { Step = assignment.GetValueOrDefault(view.Calls[i].Number, "unassigned") };
                if (view.Calls.Any(c => c.Step == "unassigned"))
                    view.Steps.Add(new("unassigned", "Другие обращения", "Запросы без назначенного этапа"));
            }
        }
        return view;
    }

    private sealed record ApiRequest(int Number, string Kind, ChatRequest Request);
    private sealed record ApiResponse(int Number, ChatResult? Response, string? Error, long Milliseconds);
}
