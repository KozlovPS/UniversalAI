using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UniversalAI.Benchmarks;
using UniversalAI.Benchmarks.Runtime;
using UniversalAI.Configuration;
using UniversalAI.Core;
using UniversalAI.Core.Capabilities;
using UniversalAI.Providers;
using UniversalAI.Services;
using UniversalAI.Services.Context;
using UniversalAI.Services.Providers;
using UniversalAI.Services.Tasks;
using UniversalAI.Web.Components;

internal static class Day15Checks
{
    public static async Task RunAsync(string root)
    {
        var at = DateTimeOffset.Parse("2026-11-22T09:00:00Z");
        var state = EvolvingMemory.Write(new(), new("deadline", "OLD"), at);
        var updated = EvolvingMemory.Write(state, new("deadline", "NEW"), at.AddHours(1));
        Check(state.Current["deadline"].Value == "OLD" && updated.Current["deadline"].Value == "NEW" &&
            updated.Audit.Last() is { Before: "OLD", After: "NEW" } && updated.Current["deadline"].WrittenAt == at.AddHours(1), "Замена не меняет исходный снимок; время и before/after сохранены");
        foreach (var time in new[] { at, at.AddHours(1) })
            updated = EvolvingMemory.Write(updated, new("deadline", "STALE", WrittenAt: time), at.AddHours(2));
        Check(updated.Current["deadline"].Value == "NEW", "Запоздавшая и равная по времени запись не заменяет факт");
        updated = EvolvingMemory.Write(updated, new("decisions", "B", "append"), at.AddHours(1));
        updated = EvolvingMemory.Write(updated, new("decisions", "A", "append"), at);
        updated = EvolvingMemory.Write(updated, new("decisions", "A", "append"), at);
        Check(updated.Lists["decisions"].Select(f => f.Value).SequenceEqual(["A", "B"]), "Список накапливается по времени без повторения одинакового решения");
        try { EvolvingMemory.Write(updated, new("deadline", "X", "task"), at); throw new Exception("Ожидался запрет смены политики"); }
        catch (ArgumentException) { }
        var prompt = EvolvingMemory.Build(updated, "{\"deadline\":\"OLD\"}", "deadline?", "fake-local");
        Check(prompt.Messages.Count == 3 && prompt.Messages.All(m => !m.Content.Contains("OLD") && !m.Content.Contains("STALE")) && prompt.MaxTokens == null && prompt.ReasoningBudgetTokens == null,
            "В промпт не входят история/audit/устаревшие значения; лимиты токенов не заданы");

        string? previous = Environment.GetEnvironmentVariable("UNIVERSALAI_DATA_DIR");
        Environment.SetEnvironmentVariable("UNIVERSALAI_DATA_DIR", Path.Combine(root, "day15"));
        var config = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json")).Build();
        var services = new ServiceCollection().AddLogging().AddUniversalAI(config).AddUniversalAIBenchmarks();
        Environment.SetEnvironmentVariable("UNIVERSALAI_DATA_DIR", previous);
        var fake = new MemoryProvider();
        services.AddSingleton<IProviderFactory>(new Factory(fake));
        await using var sp = services.BuildServiceProvider();
        await sp.InitializeUniversalAIAsync();
        var workspace = sp.GetRequiredService<IWorkspaceClient>();
        var c = await workspace.CreateAsync(new("Память", "ollama", "fake-local", "evolving"));
        foreach (var write in new[] { new MemoryWrite("deadline", "OLD", WrittenAt: at), new("deadline", "NEW", WrittenAt: at.AddHours(1)), new("temp", "SECRET", "task") })
            await workspace.SendAsync(c.Id, "/remember " + JsonSerializer.Serialize(write));
        Check(fake.Requests.Count == 0, "Явная запись работает без модельных вызовов");
        string snapshot = (await workspace.GetAsync(c.Id)).EvolvingMemoryJson;
        try { await workspace.SendAsync(c.Id, "/remember {invalid}"); throw new Exception("Ожидалась ошибка JSON"); }
        catch (ArgumentException) { }
        Check((await workspace.GetAsync(c.Id)).EvolvingMemoryJson == snapshot && fake.Requests.Count == 0,
            "Неверная JSON-команда возвращает ошибку ввода без изменения памяти и без API");
        var branch = await workspace.BranchAsync(c.Id, "Копия");
        await workspace.UpdateAsync(c.Id, new(WorkingMemoryJson: "{\"scratch\":\"WORK\"}"));
        await workspace.SendAsync(c.Id, "/remember complete");
        var info = await workspace.GetAsync(c.Id);
        Check(info.WorkingMemoryJson == "{}" && EvolvingMemory.Parse(info.EvolvingMemoryJson).Temporary.Count == 0 &&
            EvolvingMemory.Parse(info.EvolvingMemoryJson).Current["deadline"].Value == "NEW" &&
            EvolvingMemory.Parse((await workspace.GetAsync(branch.Id)).EvolvingMemoryJson).Temporary.Count == 1,
            "Очистка рабочей/временной памяти сохраняет долговременную и независимую ветку");
        await workspace.SendAsync(c.Id, "Каково актуальное значение ключа deadline? Верни только значение.");
        Check(fake.Requests.Last().Messages.All(m => !m.Content.Contains("OLD") && !m.Content.Contains("SECRET") && !m.Content.Contains("WORK")), "Обычный агент использует ту же сборку контекста, что бенчмарк");
        int messages = (await workspace.MessagesAsync(c.Id)).Total;
        fake.Fail = true;
        try { await workspace.SendAsync(c.Id, "ошибка"); throw new Exception("Ожидалась ошибка"); } catch (HttpRequestException) { }
        fake.Fail = false;
        Check((await workspace.MessagesAsync(c.Id)).Total == messages, "Ошибка API не сохраняет половину хода");
        await using (var scope = sp.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ConversationService>();
            var persisted = (await service.GetConversationAsync(c.Id))!;
            Check(EvolvingMemory.Parse(persisted.EvolvingMemoryJson).Current["deadline"].Value == "NEW", "Память перечитывается из SQLite в новом scope");
            persisted.WorkingMemoryJson = "{\"scratch\":\"WORK\"}";
            persisted.EvolvingMemoryJson = JsonSerializer.Serialize(EvolvingMemory.Write(EvolvingMemory.Parse(persisted.EvolvingMemoryJson), new("temp", "TMP", "task"), at));
            await service.SaveMessagesAsync(c.Id, [new() { Role = "assistant", Content = "Готово" }], preparedContext:
                new PreparedContext([], 0, 0, 0, 0, false, null) { PendingTaskState = new TaskWorkflowState { Goal = "test", Stage = TaskStage.Done,
                    Plan = "plan", PlanApproved = true, ApprovedPlanRevision = 1, Implementation = "code", Validation = "ok", Result = "done" } });
        }
        Check((await workspace.GetAsync(c.Id)).WorkingMemoryJson == "{}" && EvolvingMemory.Parse((await workspace.GetAsync(c.Id)).EvolvingMemoryJson).Temporary.Count == 0,
            "Завершение через штатный FSM также очищает рабочую и временную память");

        var runner = sp.GetRequiredService<BenchmarkRunner>();
        var store = sp.GetRequiredService<FileRunStore>();
        var run = await runner.ExecuteAsync(await runner.CreateAsync("day15", new() { ProviderId = "ollama", Model = "fake-local" }));
        var report = JsonSerializer.Deserialize<Day15MemoryUpdatesBenchmark.Report>(await File.ReadAllTextAsync(await store.ArtifactPathAsync(run.Id, "report.json")), FileRunStore.Json)!;
        Check(run.Status == RunStatus.Completed && report.Completed && report.Observations.Count(o => o.CallNumber != null) == 13 &&
            report.Observations.All(o => o.Passed) && report.StaleAnswers == 0 && report.PromptLeaks == 0 && report.CleanupPassed,
            "Runner: 4 замены, 13 ответов, ноль устаревших значений/утечек; очистка подтверждена");
        var artifacts = await store.ArtifactsAsync(run.Id);
        var view = await ExperimentViewData.LoadAsync(store, run, artifacts, default);
        var docs = await ExperimentDocuments.LoadAsync(store, run, sp.GetRequiredService<BenchmarkRegistry>().Get("day15").Definition, artifacts, view, default);
        Check(view.Calls.Count(o => o.Passed == true) == 13 && docs.Outcomes.Sum(o => o.Total) == report.Observations.Count + 1 &&
            docs.Outcomes.All(o => o.Passed == o.Total) && docs.Conclusion.Evidence.Contains("Устаревших ответов: 0"), "Веб-сводка показывает сохранённые оценки и счётчики, этапы ссылаются на вызовы");
        fake.Candidate = "2026-12-01";
        var hostile = await runner.ExecuteAsync(await runner.CreateAsync("day15", new() { ProviderId = "ollama", Model = "fake-local" }));
        var hostileReport = JsonSerializer.Deserialize<Day15MemoryUpdatesBenchmark.Report>(await File.ReadAllTextAsync(await store.ArtifactPathAsync(hostile.Id, "report.json")), FileRunStore.Json)!;
        Check(hostile.Status == RunStatus.Completed && hostileReport.StaleAnswers > 0 && hostileReport.Observations.Any(o => !o.Passed) && hostileReport.PromptLeaks == 0,
            "Устаревшие ответы модели считаются ошибками при успешно завершённом протоколе и чистом промпте");
        fake.Candidate = null; fake.Fail = true;
        var partial = await runner.ExecuteAsync(await runner.CreateAsync("day15", new() { ProviderId = "ollama", Model = "fake-local" }));
        var partialReport = JsonSerializer.Deserialize<Day15MemoryUpdatesBenchmark.Report>(await File.ReadAllTextAsync(await store.ArtifactPathAsync(partial.Id, "report.json")), FileRunStore.Json)!;
        Check(partial.Status == RunStatus.Failed && !partialReport.Completed && partialReport.Observations.Count == 1 && partialReport.Error != null,
            "Ошибка сохраняет частичный отчёт после первой локальной записи");
        fake.Fail = false;
        using (var cancel = new CancellationTokenSource())
        {
            fake.OnRequest = () => cancel.Cancel();
            var cancelled = await runner.ExecuteAsync(await runner.CreateAsync("day15", new() { ProviderId = "ollama", Model = "fake-local" }), ct: cancel.Token);
            fake.OnRequest = null;
            Check(cancelled.Status == RunStatus.Cancelled && File.Exists(await store.ArtifactPathAsync(cancelled.Id, "report.json")), "Отмена сохраняет частичные артефакты");
        }
        await workspace.ResetAsync(c.Id);
        Check((await workspace.GetAsync(c.Id)).EvolvingMemoryJson == "{}", "Явный полный сброс удаляет обновляемую память");
        Console.WriteLine($"Day15 offline RunId: {run.Id}; {store.GetDirectory(run.Id)}");
    }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); Console.WriteLine("OK: " + message); }
    private sealed class Factory(MemoryProvider provider) : IProviderFactory { public IAIProvider Create(ProviderProfile p) => provider; }
    internal sealed class MemoryProvider : IAIProvider
    {
        public string Name => "Memory-offline";
        public ProviderCapabilities Capabilities => new();
        public List<ChatRequest> Requests { get; } = [];
        public string? Candidate { get; set; }
        public bool Fail { get; set; }
        public Action? OnRequest { get; set; }
        public Task<ChatResult> SendMessageAsync(ChatRequest request, CancellationToken ct = default)
        {
            Requests.Add(request); OnRequest?.Invoke(); ct.ThrowIfCancellationRequested();
            if (Fail) throw new HttpRequestException("Memory test API failure");
            return Task.FromResult(new ChatResult { Content = Candidate ?? Respond(request.Messages), FinishReason = "stop", PromptTokens = 10, CompletionTokens = 5, TotalTokens = 15 });
        }
        public static string Respond(IReadOnlyList<ChatMessage> messages)
        {
            string key = messages.Last().Content.Split("ключа ")[1].Split('?')[0];
            string facts = messages[1].Content.Split("Актуальные факты:\n")[1].Split("\nРабочая память:\n")[0];
            return JsonSerializer.Deserialize<Dictionary<string, string>>(facts)!.GetValueOrDefault(key, "НЕИЗВЕСТНО");
        }
        public IAsyncEnumerable<string> StreamMessageAsync(ChatRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
