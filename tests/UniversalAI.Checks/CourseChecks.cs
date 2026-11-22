using System.Net;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UniversalAI.Benchmarks;
using UniversalAI.Benchmarks.Runtime;
using UniversalAI.Configuration;
using UniversalAI.Core;
using UniversalAI.Core.Capabilities;
using UniversalAI.Providers;
using UniversalAI.Providers.OpenAICompatible;
using UniversalAI.Services.Agents;
using UniversalAI.Services.Providers;
using UniversalAI.Web.Components;

internal static class CourseChecks
{
    public static async Task RunAsync(string root)
    {
        string? previous = Environment.GetEnvironmentVariable("UNIVERSALAI_DATA_DIR");
        Environment.SetEnvironmentVariable("UNIVERSALAI_DATA_DIR", Path.Combine(root, "course"));
        var config = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json")).Build();
        var services = new ServiceCollection().AddLogging().AddUniversalAI(config).AddUniversalAIBenchmarks();
        Environment.SetEnvironmentVariable("UNIVERSALAI_DATA_DIR", previous);
        var fake = new FoundationProvider();
        services.AddSingleton<IProviderFactory>(new Factory(fake));
        await using var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<BenchmarkRegistry>();
        Check(registry.List().Select(d => d.Id).SequenceEqual(Enumerable.Range(1, 15).Select(n => "day" + n)),
            "Каталог/API: ровно day1–day15 в числовом порядке (day9 перед day10)");
        foreach (var definition in registry.List())
        {
            var assignment = CourseAssignments.Find(definition.Id)!;
            var material = BenchmarkMaterialsCatalog.Create(definition);
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material.Assignment!))).ToLowerInvariant();
            Check(material.Assignment == assignment.Text && hash == assignment.Sha256 && assignment.Text.StartsWith("День " + assignment.Day + ".") &&
                assignment.Text.Contains("Результат:"), $"{definition.Id}: полный неизменённый исходник, SHA256 совпадает, результат задания сохранён");
        }
        Check(CourseAssignments.Find("day16") == null, "Дни без исходных условий не выдумываются");
        var sorted = new BenchmarkRegistry(new[] { "other", "day10", "day2", "day1", "day9" }.Select(id =>
            new DayBenchmark(new(id, id, "", true), (_, _) => Task.FromResult(0))));
        Check(sorted.List().Select(d => d.Id).SequenceEqual(["day1", "day2", "day9", "day10", "other"]), "Сортировка независима от порядка DI-регистрации");

        var runner = sp.GetRequiredService<BenchmarkRunner>(); var store = sp.GetRequiredService<FileRunStore>();
        foreach (int day in new[] { 1, 2, 6, 7, 8 })
        {
            int before = fake.Requests.Count;
            var run = await runner.ExecuteAsync(await runner.CreateAsync("day" + day, new() { ProviderId = "ollama", Model = "fake-local", ContextWindowTokens = 1024 }));
            var report = JsonSerializer.Deserialize<FoundationBenchmarks.Report>(await File.ReadAllTextAsync(await store.ArtifactPathAsync(run.Id, "report.json")), FileRunStore.Json)!;
            Check(run.Status == RunStatus.Completed && report.Completed && report.Observations.Count == (day == 1 ? 1 : day == 2 ? 18 : day is 6 or 7 ? 3 : 7), $"day{day}: полный исполняемый протокол, отдельный RunId и отчёт");
            if (day == 2)
            {
                var requests = fake.Requests.Skip(before).ToList();
                Check(requests.Count == 18 && requests.All(r => r.Messages.Last().Content == FoundationBenchmarks.FormatQuestion) &&
                    requests.Count(r => r.MaxTokens == 16) == 6 && requests.Count(r => r.Stop?.SingleOrDefault() == "КОНЕЦ") == 6 &&
                    requests.Count(r => r.ResponseFormat?.GetProperty("json_schema").GetProperty("strict").GetBoolean() == true) == 6,
                    "Day2: один вопрос, 6 вариантов × 3 повтора; реальная schema/stop/лимит передаются в запрос, не заменяются инструкцией");
                Check(report.Observations.Count(o => o.SchemaValid == true) == 9 && report.Observations.Count(o => o.Parseable == false) == 9,
                    "Day2: реальные строки fake парсятся кодом, повреждённый JSON и обрезка не засчитываются как успех");
            }
            else Check(report.Observations.All(o => o.Passed), $"day{day}: пройдены независимые проверки поведения");
            var artifacts = await store.ArtifactsAsync(run.Id);
            var view = await ExperimentViewData.LoadAsync(store, run, artifacts, default);
            var documents = await ExperimentDocuments.LoadAsync(store, run, registry.Get("day" + day).Definition, artifacts, view, default);
            Check(documents.ReportComplete == true && documents.Outcomes.Sum(o => o.Total) == report.Observations.Count &&
                documents.Materials.Assignment == CourseAssignments.Find("day" + day)!.Text, $"day{day}: сохранён полный текст задания, веб читает итоговые оценки");
            if (day != 2) Check(fake.Requests.Skip(before).All(r => r.MaxTokens == null && r.ReasoningBudgetTokens == null), $"day{day}: программные лимиты токенов отсутствуют");
            Console.WriteLine($"Course offline day{day}: {run.Id}; {store.GetDirectory(run.Id)}");
        }
        await CheckWireAsync();
        int checkedRequests = 0;
        foreach (string file in Directory.EnumerateFiles(root, "api-requests.jsonl", SearchOption.AllDirectories))
        {
            string metadataPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "run.json"));
            if (!File.Exists(metadataPath)) continue;
            var metadata = JsonSerializer.Deserialize<BenchmarkRun>(await File.ReadAllTextAsync(metadataPath), FileRunStore.Json)!;
            if (metadata.BenchmarkId == "day2") continue;
            foreach (string line in await File.ReadAllLinesAsync(file))
            {
                using var record = JsonDocument.Parse(line);
                var request = record.RootElement.GetProperty("request").Deserialize<ChatRequest>()!;
                if (request.MaxTokens != null || request.ReasoningBudgetTokens != null)
                    throw new InvalidOperationException($"{metadata.BenchmarkId}: незапрошенный лимит в {file}");
                checkedRequests++;
            }
        }
        Check(checkedRequests > 0, $"Проверены {checkedRequests} фактических запросов прогонов: лимиты отсутствуют вне day2");
    }
    private static async Task CheckWireAsync()
    {
        var handler = new WireHandler();
        var provider = new OpenAICompatibleProvider(new HttpClient(handler), new() { RequireApiKey = false, BaseUrl = "http://127.0.0.1:12345/v1", Name = "local", ModelCapabilities = new() });
        await provider.SendMessageAsync(new() { Model = "local-model", Messages = [new() { Role = "user", Content = "JSON" }], MaxTokens = 16, Stop = ["КОНЕЦ"], ResponseFormat = FoundationBenchmarks.ResponseSchema });
        using var json = JsonDocument.Parse(handler.Body!);
        Check(json.RootElement.GetProperty("max_tokens").GetInt32() == 16 && json.RootElement.GetProperty("stop")[0].GetString() == "КОНЕЦ" &&
            json.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean(), "HTTP body: stop/max_tokens/strict JSON Schema доходят до endpoint");
        await provider.SendMessageAsync(new() { Model = "local-model", Messages = [new() { Role = "user", Content = "без лимитов" }] });
        using var plain = JsonDocument.Parse(handler.Body!);
        Check(!plain.RootElement.TryGetProperty("max_tokens", out _) && !plain.RootElement.TryGetProperty("stop", out _) && !plain.RootElement.TryGetProperty("response_format", out _),
            "Обычный HTTP-запрос не получает скрытых лимитов/stop/schema");
    }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); Console.WriteLine("OK: " + message); }
    private sealed class Factory(FoundationProvider provider) : IProviderFactory { public IAIProvider Create(ProviderProfile p) => provider; }
    private sealed class WireHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{}\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2}}") };
        }
    }
    internal sealed class FoundationProvider : IAIProvider
    {
        public string Name => "Foundation-fake";
        public ProviderCapabilities Capabilities => new() { SupportsFunctionCalling = true, SupportsJsonSchema = true, SupportsMaxTokens = true, SupportsStopSequence = true };
        public List<ChatRequest> Requests { get; } = [];
        public Task<ChatResult> SendMessageAsync(ChatRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Requests.Add(JsonSerializer.Deserialize<ChatRequest>(JsonSerializer.Serialize(request))!);
            if (request.Messages.Last().Content.Length > 16000 && request.Tools.Count == 0) throw new HttpRequestException("maximum context length exceeded");
            return Task.FromResult(Respond(request));
        }
        public static ChatResult Respond(ChatRequest request)
        {
            var last = request.Messages.Last();
            string user = request.Messages.Last(m => m.Role == "user").Content;
            ChatResult Reply(string text) => new() { Content = text, FinishReason = "stop", PromptTokens = TokenUsageCalculator.EstimateRequest(request), CompletionTokens = 10, TotalTokens = TokenUsageCalculator.EstimateRequest(request) + 10 };
            ChatResult Tool(string name, object args) => new() { FinishReason = "tool_calls", ToolCalls = [new() { Id = Guid.NewGuid().ToString("N"), Function = new() { Name = name, Arguments = JsonSerializer.Serialize(args) } }], PromptTokens = 30, CompletionTokens = 10, TotalTokens = 40 };
            if (request.Tools.Count > 0)
            {
                if (user.Contains("до перезапуска")) return Reply("МАЯК-731, 323");
                if (user.Contains("Запомни")) return Reply("Запомнил МАЯК-731");
                if (user.Contains("ПРИВЕТ")) return Reply("ПРИВЕТ");
                if (last.Role == "tool")
                {
                    using var data = JsonDocument.Parse(last.Content);
                    if (data.RootElement.TryGetProperty("total", out var total)) return Tool("calculate", new { expression = total.GetInt32() + " * 7" });
                    return Reply(data.RootElement.GetProperty("result").ToString());
                }
                return user.Contains("roll_dice") ? Tool("roll_dice", new { sides = 6, count = 1 }) : Tool("calculate", new { expression = "17 * 19" });
            }
            if (user == FoundationBenchmarks.FormatQuestion)
            {
                if (request.MaxTokens != null) return Reply("{\"colors\":[");
                if (request.Messages.Count == 1) return Reply("Красный, жёлтый и зелёный.");
                return Reply("{\"colors\":[{\"color\":\"красный\",\"meaning\":\"стоп\"},{\"color\":\"жёлтый\",\"meaning\":\"жди\"},{\"color\":\"зелёный\",\"meaning\":\"иди\"}]}");
            }
            return Reply(user.Contains("2 + 2") || user.Contains("2+2") ? "4" : "ПРИНЯТО");
        }
        public IAsyncEnumerable<string> StreamMessageAsync(ChatRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
