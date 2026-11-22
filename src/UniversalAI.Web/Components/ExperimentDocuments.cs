using System.Text.Json;
using UniversalAI.Benchmarks.Runtime;

namespace UniversalAI.Web.Components;

public sealed class ExperimentDocuments
{
    public sealed record Outcome(string Label, int Passed, int Total, string Note);
    public ExperimentMaterials Materials { get; private set; } = new();
    public bool HasSavedMaterials { get; private set; }
    public bool IsPreview { get; private set; }
    public bool? ReportComplete { get; private set; }
    public string? ReportText { get; private set; }
    public string? ReportFile { get; private set; }
    public ExperimentConclusion Conclusion { get; private set; } = ExperimentConclusion.Empty;
    public List<Outcome> Outcomes { get; } = [];
    public List<string> Notices { get; } = [];
    public static ExperimentDocuments Preview(BenchmarkDefinition definition) => new()
    {
        Materials = BenchmarkMaterialsCatalog.Create(definition), IsPreview = true
    };
    private static JsonElement Value(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object
        ? item.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value : default;
    private static string Text(JsonElement item, string name) => Value(item, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() ?? "" : "";
    private static int? Number(JsonElement item, string name) => Value(item, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out int n) ? n : null;
    private static bool? Flag(JsonElement item, string name) => Value(item, name).ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    private static IEnumerable<JsonElement> Array(JsonElement item, string name) => Value(item, name) is { ValueKind: JsonValueKind.Array } v ? v.EnumerateArray().ToArray() : [];

    public static async Task<ExperimentDocuments> LoadAsync(FileRunStore store, BenchmarkRun run, BenchmarkDefinition definition,
        IReadOnlyList<string> artifacts, ExperimentViewData view, CancellationToken ct)
    {
        var doc = new ExperimentDocuments { Materials = BenchmarkMaterialsCatalog.Create(definition) };
        async Task<string?> Read(string name)
        {
            if (!artifacts.Contains(name)) return null;
            try { return await SnapshotFile.ReadAllTextAsync(await store.ArtifactPathAsync(run.Id, name, ct), ct); }
            catch (IOException) { doc.Notices.Add($"Файл {name} обновляется. Отображены доступные данные."); return null; }
        }
        if (await Read("experiment-materials.json") is { } saved)
        {
            try
            {
                var materials = JsonSerializer.Deserialize<ExperimentMaterials>(saved, FileRunStore.Json);
                if (materials is { SchemaVersion: 1, Groups: not null, Plan: not null } && materials.Groups.All(g => g != null && g.Items != null && g.Items.All(i => i != null)))
                { doc.Materials = materials; doc.HasSavedMaterials = true; }
                else doc.Notices.Add("Версия сохранённых материалов не поддерживается. Показаны доступные источники.");
            }
            catch (JsonException) { doc.Notices.Add("Снимок материалов пока не прочитан полностью."); }
        }
        doc.ReportText = await Read("report.md");
        doc.ReportFile = doc.ReportText == null ? null : "report.md";
        JsonElement report = default;
        if (await Read("report.json") is { } json)
        {
            try { using var parsed = JsonDocument.Parse(json); report = parsed.RootElement.Clone(); }
            catch (JsonException) { doc.Notices.Add("Отчёт ещё записывается. Оценки показаны только по доступным данным."); }
        }
        doc.ReportComplete = Flag(report, "Completed") ?? Flag(report, "Complete");
        if (!doc.HasSavedMaterials && run.BenchmarkId == "day11" && view.Layers == null)
            doc.Materials = doc.Materials with { Groups = [] };
        if (run.BenchmarkId == "day11" && view.Layers is { } layers)
        {
            doc.ReportComplete = layers.Completed;
            foreach (var group in layers.Observations.GroupBy(o => o.Stage))
                doc.Outcomes.Add(new(ExperimentViewData.LayerStageName(group.Key), group.Count(o => o.Passed), group.Count(),
                    $"Доступные факты: {group.Count(o => o.FactAvailable && o.Passed)}/{group.Count(o => o.FactAvailable)} · После удаления: {group.Count(o => !o.FactAvailable && o.Passed)}/{group.Count(o => !o.FactAvailable)}"));
            if (!doc.HasSavedMaterials && layers.Snapshots.FirstOrDefault() is { } initial)
            {
                doc.Materials = doc.Materials with { Groups = [] };
                doc.Materials.Groups.Add(new("Текущий диалог", "Исходный снимок из отчёта этого прогона.", initial.ShortTerm.Select(m => new ExperimentMaterialItem(m.Role, m.Content)).ToList()));
                doc.Materials.Groups.Add(new("Рабочая память", "Данные задачи в исходном снимке.", initial.Working.Select(p => new ExperimentMaterialItem(p.Key, p.Value)).ToList()));
                doc.Materials.Groups.Add(new("Долговременные facts", "Явно сохранённые факты исходного снимка.", initial.LongTerm.Select(p => new ExperimentMaterialItem(p.Key, p.Value)).ToList()));
                doc.Materials.Groups.Add(new("Контрольные вопросы", "Эталоны из сохранённых наблюдений; после удаления источника ожидается НЕИЗВЕСТНО.", layers.Observations.DistinctBy(o => o.ProbeId).Select(o => new ExperimentMaterialItem(o.Layer, o.Question, o.Expected)).ToList()));
            }
        }
        if (view.Personalization is { } personalization)
        {
            doc.ReportComplete = personalization.Completed;
            foreach (var group in personalization.Observations.GroupBy(o => o.Stage))
                doc.Outcomes.Add(new(group.Key == "stability" ? "Устойчивость · 10 вопросов" : $"Общий вопрос · {group.First().ProfileName}",
                    group.Count(o => o.Assessment.Passed), group.Count(),
                    $"Объём: {group.Count(o => o.Assessment.LengthPassed)}/{group.Count()} · Код: {group.Count(o => o.Assessment.CodePassed)}/{group.Count()} · Тон: {group.Count(o => o.Assessment.TonePassed)}/{group.Count()}"));
        }
        if (view.TaskWorkflow is { } workflow)
        {
            doc.ReportComplete = workflow.Completed;
            foreach (var group in workflow.Observations.GroupBy(o => o.Group))
                doc.Outcomes.Add(new(group.Key, group.Count(o => o.Passed), group.Count(),
                    $"Выбор действия моделью: {group.Count(o => !o.Input.StartsWith("/task ") && o.IntentMatched)}/{group.Count(o => !o.Input.StartsWith("/task "))} · Отказов: {group.Count(o => !o.Turn.Allowed)} · Состояние после отказов сохранено: {group.All(o => o.StatePreservedOnDenial)}"));
        }
        if (view.Invariants is { } invariants)
        {
            doc.ReportComplete = invariants.Completed;
            foreach (var group in invariants.Observations.GroupBy(o => o.Group))
            {
                string label = group.Key switch
                {
                    "setup" => "Задание инвариантов", "control" => "Совместимый запрос",
                    "attacks" => "Попытки обхода", "change" => "Явная замена правила",
                    "updated" => "Ответ после замены", _ => group.Key
                };
                doc.Outcomes.Add(new(label, group.Count(o => o.Passed), group.Count(),
                    group.Key == "attacks"
                        ? $"Проверено попыток: {invariants.Attempts}; опубликовано нарушений: {invariants.Bypasses}; отказов модели: {invariants.ModelRefusals}; блокировок кодом: {invariants.CodeBlocks}."
                        : "Сохранённые оценки шагов; ответы повторно не оцениваются."));
            }
        }
        if (view.MemoryUpdates is { } updates)
        {
            doc.ReportComplete = updates.Completed;
            foreach (var group in updates.Observations.GroupBy(o => o.Group))
                doc.Outcomes.Add(new(group.Key, group.Count(o => o.Passed), group.Count(),
                    $"Устаревших ответов: {group.Count(o => o.StaleAnswer)}; утечек в промпт: {group.Count(o => o.PromptLeak)}."));
            if (updates.Completed) doc.Outcomes.Add(new("Очистка слоёв", updates.CleanupPassed ? 1 : 0, 1,
                "Рабочая и временная память очищены; текущие факты и списки долговременной памяти сохранены."));
        }
        foreach (var result in Array(report, "Results"))
        {
            if (Number(result, "CorrectTasks") is { } passed && Number(result, "TaskCount") is { } total)
                doc.Outcomes.Add(new(Text(result, "ModelId") is { Length: > 0 } model ? model : $"{Text(result, "StrategyLabel")} · {Text(result, "ReasoningLabel")}", passed, total,
                    $"Ошибок запросов в отчёте: {Number(result, "FailedTasks")?.ToString() ?? "Н/д"}"));
            if (run.BenchmarkId == "day4" && Number(result, "CorrectCount") is { } correct && Number(report, "Repetitions") is { } repetitions)
                doc.Outcomes.Add(new($"temperature={Value(result, "Temperature")} · {Text(result, "TaskLabel")}", correct, repetitions,
                    $"Ошибок API: {Number(result, "FailedCalls")?.ToString() ?? "Н/д"} · Сбоев формата: {Number(result, "FormatFailures")?.ToString() ?? "Н/д"}"));
        }
        foreach (var strategy in Array(report, "Strategies"))
        {
            foreach (string section in new[] { "Answers", "Branches" })
            {
                var rows = Array(strategy, section).ToList();
                if (rows.Count > 0) doc.Outcomes.Add(new($"{Value(strategy, "Strategy")} · {(section == "Answers" ? "основной диалог" : "ветки")}", rows.Count(r => Flag(r, "Correct") == true), rows.Count, "Оценки сохранённого отчёта"));
            }
        }
        var probes = Array(report, "Probes").ToList();
        if (probes.Count > 0)
            foreach (var mode in new[] { ("Полная история", "FullCorrect"), ("Сжатый контекст", "CompressedCorrect") })
                doc.Outcomes.Add(new(mode.Item1, probes.Count(p => Flag(p, mode.Item2) == true), probes.Count, "Сохранённая проверка фактов"));

        if ((!doc.HasSavedMaterials || doc.Materials.Groups.Count == 0) && run.BenchmarkId != "day11")
        {
            doc.Materials = doc.Materials with { Groups = [] };
            var cases = Array(report, "Results").SelectMany(r => Array(r, "Cases")).Where(c => Text(c, "Question").Length > 0)
                .DistinctBy(c => (Text(c, "TaskId"), Text(c, "Question"))).ToList();
            if (cases.Count > 0) doc.Materials.Groups.Add(new("Проверочные задачи", "Формулировки и эталоны именно из отчёта выбранного прогона.", cases.Select(c => new ExperimentMaterialItem(Text(c, "TaskId"), Text(c, "Question"), string.Join(" / ", Array(c, "ExpectedAnswers").Select(x => x.GetString())))).ToList()));
            if (await Read("transcript.json") is { } transcript)
            {
                try
                {
                    using var parsed = JsonDocument.Parse(transcript);
                    if (parsed.RootElement.ValueKind == JsonValueKind.Array)
                        doc.Materials.Groups.Add(new("Исходный диалог", "Реплики пользователя из сохранённого transcript.json.", parsed.RootElement.EnumerateArray().Where(m => Text(m, "role") == "user").Select((m, i) => new ExperimentMaterialItem($"Реплика {i + 1}", Text(m, "content"))).ToList()));
                }
                catch (JsonException) { doc.Notices.Add("Исходный диалог ещё записывается."); }
            }
            if (await Read("scenario.json") is { } scenario)
            {
                try
                {
                    using var parsed = JsonDocument.Parse(scenario);
                    var checks = Array(parsed.RootElement, "Probes").ToList();
                    if (checks.Count > 0) doc.Materials.Groups.Add(new("Контрольные вопросы", "Вопросы и эталоны из scenario.json.", checks.Select(p => new ExperimentMaterialItem(Text(p, "Detail"), Text(p, "Question"), Text(p, "Expected"))).ToList()));
                }
                catch (JsonException) { doc.Notices.Add("Сценарий ещё записывается."); }
            }
        }
        if (doc.Materials.Groups.Count == 0 || doc.Materials.Groups.All(g => g.Items.Count == 0))
            doc.Materials.Groups.Add(new("Входные запросы", "Текст из записанных запросов API. Эталон не восстанавливается из ответов модели.", view.Calls.Select(c => c.Request.Messages.LastOrDefault(m => m.Role == "user"))
                .Where(m => m != null).DistinctBy(m => m!.Content).Select((m, i) => new ExperimentMaterialItem($"Вход {i + 1}", m!.Content)).ToList()));
        if (view.Calls.FirstOrDefault()?.Request.Messages.FirstOrDefault(m => m.Role == "system") is { } rule)
            doc.Materials.Groups.Add(new("Правило ответа", "Системная инструкция из первого сохранённого запроса.", [new("system", rule.Content)]));
        doc.Conclusion = ExperimentConclusion.Create(run.BenchmarkId, view, doc.Outcomes);
        return doc;
    }
}
