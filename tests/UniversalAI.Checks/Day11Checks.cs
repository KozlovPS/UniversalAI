using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UniversalAI.Benchmarks;
using UniversalAI.Benchmarks.Runtime;
using UniversalAI.Configuration;
using UniversalAI.Core;
using UniversalAI.Core.Capabilities;
using UniversalAI.Providers;
using UniversalAI.Providers.OpenAICompatible;
using UniversalAI.Services;
using UniversalAI.Services.Context;
using UniversalAI.Services.Providers;

internal static class Day11Checks
{
    public static async Task RunAsync(string root)
    {
        string? previousRoot = Environment.GetEnvironmentVariable("UNIVERSALAI_DATA_DIR");
        Environment.SetEnvironmentVariable("UNIVERSALAI_DATA_DIR", Path.Combine(root, "day11"));
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json")).Build();
        var fake = new MemoryProvider();
        var services = new ServiceCollection().AddLogging().AddUniversalAI(configuration).AddUniversalAIBenchmarks();
        Environment.SetEnvironmentVariable("UNIVERSALAI_DATA_DIR", previousRoot);
        services.AddSingleton<IProviderFactory>(new MemoryFactory(fake));
        await using var sp = services.BuildServiceProvider();
        await sp.InitializeUniversalAIAsync();
        var workspace = sp.GetRequiredService<IWorkspaceClient>();
        var chat = await workspace.CreateAsync(new("Слои", "ollama", "fake-local"));
        string work = JsonSerializer.Serialize(Day11MemoryLayersBenchmark.InitialWorking());
        string facts = JsonSerializer.Serialize(Day11MemoryLayersBenchmark.InitialLongTerm());
        await workspace.UpdateAsync(chat.Id, new(MemoryStrategy: "Layered", WorkingMemoryJson: work, LongTermMemoryJson: facts));
        foreach (string agent in new[] { "direct", "tools" })
        {
            await workspace.UpdateAsync(chat.Id, new(AgentId: agent));
            var turn = await workspace.SendAsync(chat.Id, "Временный код: СОСНА-731");
            Check(turn.Context?.LayerTokens is { Working: > 0, LongTerm: > 0 } &&
                fake.Requests.Last().Messages.Any(m => m.Content.Contains("LAB-118")) &&
                fake.Requests.Last().Messages.Any(m => m.Content.Contains("420000")),
                $"{agent}: слои реально включены в запрос и имеют отдельные оценки токенов");
        }
        var info = await workspace.GetAsync(chat.Id);
        Check(info.WorkingMemoryJson == work && info.FactsJson == facts, "Слои восстановлены новым scope; обычные ответы не меняют выбранные данные");
        foreach (string invalid in new[] { "[]", "{\"x\":1}", "{\"x\":\"a\",\"x\":\"b\"}", "broken" })
        {
            bool failed = false;
            try { await workspace.UpdateAsync(chat.Id, new(Title: "не сохранить", WorkingMemoryJson: invalid)); }
            catch (ArgumentException) { failed = true; }
            Check(failed && (await workspace.GetAsync(chat.Id)).Title == "Слои", "Невалидная память отклоняется до изменения настроек");
        }
        var clone = await workspace.BranchAsync(chat.Id, "Копия слоёв");
        await workspace.UpdateAsync(chat.Id, new(ClearShortTerm: true));
        Check((await workspace.MessagesAsync(chat.Id)).Total == 0 && (await workspace.GetAsync(chat.Id)).WorkingMemoryJson == work &&
            (await workspace.GetAsync(chat.Id)).FactsJson == facts, "Очистка диалога сохраняет рабочую и долговременную память");
        await workspace.UpdateAsync(chat.Id, new(WorkingMemoryJson: "{}"));
        Check((await workspace.GetAsync(chat.Id)).FactsJson == facts, "Очистка задачи сохраняет долговременные facts");
        await workspace.UpdateAsync(chat.Id, new(LongTermMemoryJson: "{}"));
        var empty = await workspace.SendAsync(chat.Id, "Какой утверждённый бюджет в рублях?");
        Check(empty.Response.Content == "НЕИЗВЕСТНО" && empty.Context?.LayerTokens?.Total == 0,
            "После очистки всех слоёв удалённые данные не отправляются модели");
        Check((await workspace.GetAsync(clone.Id)).WorkingMemoryJson == work &&
            (await workspace.GetAsync(clone.Id)).FactsJson == facts && (await workspace.MessagesAsync(clone.Id)).Total == 4,
            "Очистка слоёв не изменяет независимую ветку");
        await workspace.UpdateAsync(clone.Id, new(CompressionEnabled: false));
        var disabled = await workspace.SendAsync(clone.Id, "Без памяти");
        Check(disabled.Context?.LayerTokens == null && !fake.Requests.Last().Messages.Any(m => m.Content.Contains("LAB-118")),
            "Отключение памяти исключает явные слои из промпта");
        await workspace.ResetAsync(clone.Id);
        Check((await workspace.GetAsync(clone.Id)).WorkingMemoryJson == "{}" && (await workspace.GetAsync(clone.Id)).FactsJson == "{}",
            "Полный reset очищает все слои");

        // Day-10 facts are the same persisted dictionary, not a second unsynchronised memory.
        await workspace.UpdateAsync(chat.Id, new(LongTermMemoryJson: facts, WorkingMemoryJson: work));
        await workspace.UpdateAsync(chat.Id, new(MemoryStrategy: "StickyFacts"));
        await workspace.UpdateAsync(chat.Id, new(MemoryStrategy: "Layered"));
        Check((await workspace.GetAsync(chat.Id)).FactsJson == facts, "Переход от facts Дня 10 к Layered сохраняет те же долговременные данные");
        var runner = sp.GetRequiredService<BenchmarkRunner>();
        var store = sp.GetRequiredService<FileRunStore>();
        fake.Requests.Clear();
        var run = await runner.CreateAsync("day11", new() { ProviderId = "ollama", Model = "fake-local" });
        run = await runner.ExecuteAsync(run);
        string path = await store.ArtifactPathAsync(run.Id, "report.json");
        var report = JsonSerializer.Deserialize<Day11MemoryLayersBenchmark.Report>(await File.ReadAllTextAsync(path))!;
        var materials = JsonSerializer.Deserialize<ExperimentMaterials>(
            await File.ReadAllTextAsync(await store.ArtifactPathAsync(run.Id, "experiment-materials.json")), FileRunStore.Json)!;
        Check(materials.SchemaVersion == 1 && materials.Assignment?.Contains("Разделите информацию") == true &&
            materials.Groups.SelectMany(g => g.Items).Any(i => i.Text.Contains("СОСНА-731")),
            "Материалы и задание Day-11 сохраняются в артефактах конкретного прогона");
        Check(run.Status == RunStatus.Completed && run.CompletedCalls == 24 && report.Completed &&
            report.Observations.Count == 24 && report.Observations.All(o => o.Passed),
            "Day-11: 24 независимых вопроса на 4 этапах, сохранение/забывание проверены раздельно");
        Check(fake.Requests.All(r => r.MaxTokens == null && r.ReasoningBudgetTokens == null),
            "Day-11 не задаёт лимит ответа или бюджет reasoning без запроса пользователя");
        Check(run.ScenarioVersion == "2" && report.ScenarioVersion == run.ScenarioVersion,
            "Day-11: версия сценария без лимита согласована в метаданных и отчёте");
        using (var http = new HttpClient(new UnlimitedRequestHandler()))
        {
            var transport = new OpenAICompatibleProvider(http, new() { Name = "Offline-wire", BaseUrl = "http://localhost/v1",
                RequireApiKey = false, ApiKeyEnvironmentVariable = "", ModelCapabilities = new() });
            await transport.SendMessageAsync(fake.Requests[0]);
        }
        Console.WriteLine("OK: Day-11: HTTP-запрос не содержит max_tokens, max_completion_tokens и reasoning_budget_tokens");
        Check(report.Snapshots.Count == 4 && report.Snapshots[0].Tokens.Total > 0 && report.Snapshots[3].Tokens.Total == 0 &&
            report.Snapshots[1].Tokens.ShortTerm == 0 && report.Snapshots[2].Tokens.Working == 0,
            "Оценки слоёв обнуляются после их очистки; снимки сохранены");
        Check(fake.Requests.Skip(6).All(r => !r.Messages.Any(m => m.Content.Contains("СОСНА-731"))) &&
            fake.Requests.Skip(12).All(r => !r.Messages.Any(m => m.Content.Contains("LAB-118"))) &&
            fake.Requests.Skip(18).All(r => !r.Messages.Any(m => m.Content.Contains("420000"))),
            "Фактические запросы не содержат очищенных данных и ответов предыдущих probes");
        string log = await File.ReadAllTextAsync(Path.Combine(store.GetDirectory(run.Id), "output.log"));
        Check(log.Contains("ВОПРОС:") && log.Contains("ОТВЕТ:") && log.Contains("ОЖИДАНИЕ:") && log.Contains("ЭТАП clear-long"),
            "Пошаговый журнал показывает процесс, ожидания и оценивание для записи видео");
        fake.FailAt = fake.Requests.Count + 2;
        var partial = await runner.CreateAsync("day11", new() { ProviderId = "ollama", Model = "fake-local" });
        partial = await runner.ExecuteAsync(partial);
        var partialReport = JsonSerializer.Deserialize<Day11MemoryLayersBenchmark.Report>(
            await File.ReadAllTextAsync(await store.ArtifactPathAsync(partial.Id, "report.json")))!;
        Check(partial.Status == RunStatus.Failed && !partialReport.Completed && partialReport.Observations.Count == 1 && partialReport.Error != null,
            "Ошибка API сохраняет частичный отчёт, а не завершённое измерение");
        Console.WriteLine($"Day-11 offline RunId: {run.Id}; артефакты: {store.GetDirectory(run.Id)}");
    }

    private static void Check(bool ok, string text)
    { if (!ok) throw new InvalidOperationException(text); Console.WriteLine("OK: " + text); }

    private sealed class MemoryFactory(MemoryProvider provider) : IProviderFactory
    { public IAIProvider Create(ProviderProfile profile) => provider; }
    private sealed class UnlimitedRequestHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Check(!json.RootElement.TryGetProperty("max_tokens", out _) &&
                !json.RootElement.TryGetProperty("max_completion_tokens", out _) &&
                !json.RootElement.TryGetProperty("reasoning_budget_tokens", out _),
                "Провайдер не подставляет ограничение в HTTP-запрос Day-11");
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(
                """{"choices":[{"message":{"content":"НЕИЗВЕСТНО"},"finish_reason":"stop"}]}""", System.Text.Encoding.UTF8, "application/json") };
        }
    }
    private sealed class MemoryProvider : IAIProvider
    {
        public List<ChatRequest> Requests { get; } = [];
        public int? FailAt { get; set; }
        public string Name => "Memory-test";
        public ProviderCapabilities Capabilities => new() { SupportsFunctionCalling = true };
        public Task<ChatResult> SendMessageAsync(ChatRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (Requests.Count == FailAt) throw new HttpRequestException("Test failure");
            var probe = Day11MemoryLayersBenchmark.Probes.FirstOrDefault(p => p.Question == request.Messages.Last().Content);
            string context = string.Join("\n", request.Messages.Skip(1).SkipLast(1).Select(m => m.Content));
            context = System.Text.RegularExpressions.Regex.Unescape(context);
            string answer = probe == null ? "Принято." : context.Contains(probe.Value, StringComparison.Ordinal) ? probe.Value : "НЕИЗВЕСТНО";
            return Task.FromResult(new ChatResult { Content = answer, FinishReason = "stop", PromptTokens = 100, CompletionTokens = 10, TotalTokens = 110 });
        }
        public IAsyncEnumerable<string> StreamMessageAsync(ChatRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
