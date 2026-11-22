using System.Text;
using System.Text.Json;
using UniversalAI.Benchmarks.Runtime;
using UniversalAI.Services.Context;

namespace UniversalAI.Benchmarks;

public static class Day15MemoryUpdatesBenchmark
{
    public const string ScenarioVersion = "2", ScorerVersion = "1";
    public static readonly string[] Keys = ["deadline", "requirement", "approach", "version"];
    public static readonly string[] Initial = ["2026-12-01", "EXPORT_REQUIRED", "MICROSERVICES", "VERSION_1"];
    public static readonly string[] Updated = ["2026-12-20", "EXPORT_CANCELLED", "MODULAR_MONOLITH", "VERSION_2"];
    public sealed record Observation(int Number, int? CallNumber, string Group, string Input, string Expected,
        MemoryTurn Turn, bool StaleAnswer, bool PromptLeak, bool Passed);
    public sealed record Report(bool Completed, string? Error, List<Observation> Observations,
        EvolvingState State, string WorkingJson, int StaleAnswers, int PromptLeaks, bool CleanupPassed);

    public static async Task<int> ExecuteAsync(BenchmarkSettings settings, BenchmarkExecutionContext context)
    {
        var provider = context.CreateProvider();
        var state = new EvolvingState();
        string working = "{\"scratch\":\"WORKING_ONLY\"}";
        List<Observation> observations = [];
        var presentation = new ExperimentPresentation();
        bool complete = false, cleanup = false;
        string? error = null;
        int calls = 0;
        var at = DateTimeOffset.Parse("2026-11-22T09:00:00Z");
        context.Output.WriteLine($"ДЕНЬ 15 · Обновление памяти\nМодель: {settings.Model}; 13 запросов, один диалог, 4 изменяемых факта.\nПлан: исходные значения → 4 замены → запоздавшая запись → списки → завершение задачи.\nreplace: срок/требование/подход/версия; append: решения; task: временные факты. Все записи с временем.\nВ модель: актуальные значения + рабочая память + текущий вопрос. История и аудит исключены. Ограничения токенов не задаются.\nАртефакты: report.json/md, experiment-view.json, api-requests/calls.jsonl, снимок тарифов.");
        await SaveAsync();
        try
        {
            for (int i = 0; i < Keys.Length; i++)
            {
                await WriteAsync(Keys[i], Initial[i], "replace", at, "initial");
                await ProbeAsync("initial", Keys[i], Initial[i], []);
            }
            for (int i = 0; i < Keys.Length; i++)
            {
                await WriteAsync(Keys[i], Updated[i], "replace", at.AddHours(1), "update");
                await ProbeAsync("update", Keys[i], Updated[i], Initial.Take(i + 1).ToArray());
            }
            await WriteAsync(Keys[0], Initial[0], "replace", at.AddMinutes(30), "late-write");
            await ProbeAsync("late-write", Keys[0], Updated[0], Initial);
            await WriteAsync("decisions", "DECISION_A", "append", at, "lists");
            await WriteAsync("decisions", "DECISION_B", "append", at.AddHours(1), "lists");
            await ProbeAsync("lists", "decisions", "DECISION_A, DECISION_B", Initial);
            await WriteAsync("temporary", "TASK_ONLY", "task", at, "cleanup");
            string durable = JsonSerializer.Serialize(new { state.Current, state.Lists });
            await StepAsync("cleanup", "/remember complete", "Рабочая и временная память очищены", [], false);
            cleanup = working == "{}" && state.Temporary.Count == 0 &&
                durable == JsonSerializer.Serialize(new { state.Current, state.Lists });
            await ProbeAsync("cleanup", "temporary", "НЕИЗВЕСТНО", [.. Initial, "TASK_ONLY", "WORKING_ONLY"]);
            await ProbeAsync("cleanup", "scratch", "НЕИЗВЕСТНО", [.. Initial, "TASK_ONLY", "WORKING_ONLY"]);
            await ProbeAsync("cleanup", "deadline", Updated[0], [.. Initial, "TASK_ONLY", "WORKING_ONLY"]);
            complete = true;
            await SaveAsync();
        }
        catch (Exception e) { error = e.Message; await SaveAsync(); throw; }
        context.Output.WriteLine($"ИТОГ: устаревших ответов {observations.Count(o => o.StaleAnswer)}, промптов с устаревшими значениями {observations.Count(o => o.PromptLeak)}, очистка {cleanup}; проверки {observations.Count(o => o.Passed)}/{observations.Count}. Протокол завершён; качество оценивается отдельно.");
        return 0;

        Task WriteAsync(string key, string value, string policy, DateTimeOffset time, string group) =>
            StepAsync(group, "/remember " + JsonSerializer.Serialize(new MemoryWrite(key, value, policy, time), EvolvingMemory.Json),
                group == "late-write" ? "Сохранено " + Updated[0] + "; более старая запись отклонена" : value, [], false);
        Task ProbeAsync(string group, string key, string expected, string[] forbidden) =>
            StepAsync(group, $"Каково актуальное значение ключа {key}? Верни только значение.", expected, forbidden, true);
        async Task StepAsync(string group, string input, string expected, string[] forbidden, bool probe)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var stage = new ExperimentStage { Id = "step-" + (observations.Count + 1), Title = $"{observations.Count + 1}. {group}", Description = input };
            presentation.Stages.Add(stage);
            string before = JsonSerializer.Serialize(new { state, working }, EvolvingMemory.Json);
            int? call = probe ? ++calls : null;
            if (call.HasValue) stage.CallNumbers.Add(call.Value);
            context.Output.WriteLine($"\nЭТАП {stage.Title}\nВХОД: {input}\nОЖИДАНИЕ: {expected}\nДО: {before}");
            if (probe) context.Output.WriteLine("СОСТАВ КОНТЕКСТА: 3 сообщения — инструкции, текущие факты/рабочие данные, вопрос. История=0; audit=0. API #" + call);
            await SaveAsync();
            var turn = await EvolvingMemory.RunAsync(state, working, input, settings.Model!, () => provider, context.CancellationToken);
            state = turn.State;
            if (input == "/remember complete") working = "{}";
            bool stale = probe && forbidden.Any(v => turn.Answer.Contains(v, StringComparison.OrdinalIgnoreCase));
            bool leak = turn.Request != null && turn.Request.Messages.Any(m => forbidden.Any(v => m.Content.Contains(v, StringComparison.OrdinalIgnoreCase)));
            bool passed = !stale && !leak && (probe ? turn.Answer.Trim().Trim('"', '`', '.', ' ', '\r', '\n').Equals(expected, StringComparison.OrdinalIgnoreCase)
                : input == "/remember complete" ? state.Temporary.Count == 0 && working == "{}"
                : group == "late-write" ? state.Current[Keys[0]].Value == Updated[0]
                : state.Current.Values.Concat(state.Temporary.Values).Any(f => f.Value == expected) || state.Lists.Values.Any(l => l.Any(f => f.Value == expected)));
            observations.Add(new(observations.Count + 1, call, group, input, expected, turn, stale, leak, passed));
            stage.Changes.Add(new("Память и журнал", before, JsonSerializer.Serialize(new { state, working }, EvolvingMemory.Json)));
            stage.Metrics.Add(new("Ответ", turn.Answer));
            stage.Metrics.Add(new("Проверка", passed ? "Пройдена" : "Расхождение"));
            stage.Metrics.Add(new("Устаревший ответ / утечка в промпт", $"{stale} / {leak}"));
            context.Output.WriteLine($"ОТВЕТ: {turn.Answer}\nПРОВЕРКА: {passed}; устаревший ответ: {stale}; старые значения в промпте: {leak}");
            await SaveAsync();
        }
        async Task SaveAsync()
        {
            Directory.CreateDirectory(context.Directory);
            var report = new Report(complete, error, observations, state, working, observations.Count(o => o.StaleAnswer), observations.Count(o => o.PromptLeak), cleanup);
            await WriteFile("report.json", JsonSerializer.Serialize(report, FileRunStore.Json));
            await WriteFile("experiment-view.json", JsonSerializer.Serialize(presentation, FileRunStore.Json));
            var md = new StringBuilder("# День 15 — обновление памяти\n\n")
                .AppendLine($"Протокол завершён: {complete}. Модель: {settings.Model}. Версии: {ScenarioVersion}/{ScorerVersion}. Ошибка: {error ?? "нет"}.")
                .AppendLine($"Проверки: {observations.Count(o => o.Passed)}/{observations.Count}. Устаревшие ответы: {report.StaleAnswers}. Утечки в промпт: {report.PromptLeaks}. Очистка: {cleanup}.")
                .AppendLine("Один диалог с явными записями; история хранится, но не передаётся модели. Списки намеренно сохраняют решения; аудит замен исключён из промпта. Оценка ответов — точное равенство после удаления внешней пунктуации, не семантический судья.");
            foreach (var o in observations) md.AppendLine($"\n## {o.Number}. {o.Group}\n\n{o.Input}\n\nОжидание: {o.Expected}\n\nОтвет: {o.Turn.Answer}\n\nПройдено: {o.Passed}; устаревший ответ: {o.StaleAnswer}; утечка: {o.PromptLeak}.");
            await WriteFile("report.md", md.ToString());
        }
        Task WriteFile(string name, string text) => SnapshotFile.WriteAllTextAsync(Path.Combine(context.Directory, name), text, CancellationToken.None);
    }
    public static Task<int> RunAsync(string[] args)
    {
        Console.WriteLine("День 15: dotnet run -- --benchmark day15 --provider proxyapi --model deepseek/deepseek-v4.1-flash --auto\nOffline: dotnet run --project tests/UniversalAI.Checks");
        return Task.FromResult(args.Any(a => a is "--help" or "-h" or "help") ? 0 : 2);
    }
}
