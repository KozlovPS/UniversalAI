using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using UniversalAI.Benchmarks;
using UniversalAI.Benchmarks.Runtime;
using UniversalAI.Configuration;
using UniversalAI.Core;
using UniversalAI.Core.Capabilities;
using UniversalAI.Data;
using UniversalAI.Providers;
using UniversalAI.Providers.OpenAICompatible;
using UniversalAI.Services;
using UniversalAI.Services.Providers;

if (args.FirstOrDefault() == "--serve-fake")
{
    var builder = WebApplication.CreateBuilder(args[1..]);
    var app = builder.Build();
    app.MapGet("/v1/models", () => Results.Json(new { data = new[] { new { id = "fake-local" } } }));
    app.MapPost("/v1/chat/completions", async (HttpRequest request) =>
    {
        using var body = await JsonDocument.ParseAsync(request.Body);
        string last = body.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString() ?? "";
        string content = last.Contains("ANSWER", StringComparison.Ordinal) ? "ANSWER: 323" : "Тестовый ответ локального сервера.";
        return Results.Json(new { choices = new[] { new { message = new { role = "assistant", content }, finish_reason = "stop" } }, usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 } });
    });
    await app.RunAsync(); return;
}

string root = Path.GetFullPath(Path.Combine(".local", "checks", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("UNIVERSALAI_DATA_DIR", root);
var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
    .AddInMemoryCollection(new Dictionary<string, string?> { ["Providers:Default"] = "ollama" }).Build();
var services = new ServiceCollection().AddLogging().AddUniversalAI(configuration).AddUniversalAIBenchmarks();
var fake = new FakeProvider();
services.AddSingleton<IProviderFactory>(new FakeFactory(fake));
await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
await provider.InitializeUniversalAIAsync();
var workspace = provider.GetRequiredService<IWorkspaceClient>();
var store = provider.GetRequiredService<FileRunStore>();
var runner = provider.GetRequiredService<BenchmarkRunner>();

var chat = await workspace.CreateAsync(new("Проверка", "ollama", "fake-local"));
await Task.WhenAll(workspace.SendAsync(chat.Id, "first"), workspace.SendAsync(chat.Id, "second"));
var messages = await workspace.MessagesAsync(chat.Id);
Check(messages.Total == 4 && fake.MaxConcurrent == 1 && fake.RequestSizes.Contains(4), "Одновременные ходы одного диалога выполняются последовательно");
Check((await workspace.MessagesAsync(chat.Id, 1, 2)).Items.Count == 2, "История читается страницами");
var clone = await workspace.BranchAsync(chat.Id, "Ветка");
await workspace.SendAsync(clone.Id, "branch-only");
Check((await workspace.MessagesAsync(chat.Id)).Total == 4 && (await workspace.MessagesAsync(clone.Id)).Total == 6, "Ветка копирует историю без общей изменяемой памяти");
await ExpectFailure(() => workspace.UpdateAsync(chat.Id, new(Temperature: double.NaN)), "NaN отклоняется на уровне приложения");
await ExpectFailure(() => workspace.UpdateAsync(chat.Id, new(Title: "не сохранять", MaxTokens: -1)), "Некорректное обновление не меняет настройки");
Check((await workspace.GetAsync(chat.Id)).Title == "Проверка", "После ошибки исходные настройки сохранены");

await workspace.UpdateAsync(chat.Id, new(MemoryStrategy: "StickyFacts"));
await workspace.SendAsync(chat.Id, "budget=420000");
Check((await workspace.GetAsync(chat.Id)).FactsJson.Contains("420000"), "Sticky Facts работает в обычном чате и сохраняется");
Check((await workspace.TokensAsync(chat.Id)).MemoryPromptTokens == 100, "Usage Sticky Facts учитывается отдельно от ответов");
await workspace.ResetAsync(chat.Id);
Check((await workspace.GetAsync(chat.Id)).FactsJson == "{}" && (await workspace.MessagesAsync(chat.Id)).Total == 0, "Reset очищает журнал и facts");
fake.FailAnswer = true;
await ExpectFailure(() => workspace.SendAsync(chat.Id, "budget=420000"), "Ошибка основного ответа возвращается вызывающему клиенту");
fake.FailAnswer = false;
Check((await workspace.GetAsync(chat.Id)).FactsJson == "{}" && (await workspace.MessagesAsync(chat.Id)).Total == 0,
    "Ошибка ответа не сохраняет факты из несостоявшегося хода");

using (var http = new HttpClient(new StubHttpHandler()))
{
    var local = new OpenAICompatibleProvider(http, new() { Name = "Local", BaseUrl = "http://localhost:1234/v1", DefaultModel = "unknown-local", RequireApiKey = false, SupportsTools = false, ApiKeyEnvironmentVariable = "", ModelCapabilities = new() });
    var response = await local.SendMessageAsync(new() { Model = "unknown-local", Messages = [new() { Role = "user", Content = "hello" }] });
    Check(response.Content == "local-ok" && !local.Capabilities.SupportsFunctionCalling, "Локальный endpoint работает без ключа и отдельного профиля в коде");
}

fake.Reset();
var run = await runner.CreateAsync("day4", new() { ProviderId = "ollama", Model = "fake-local" });
run = await runner.ExecuteAsync(run);
Check(run.Status == RunStatus.Completed && run.CompletedCalls == 30, "Day-4 выполняется через общий runner и локальный профиль");
Check((await store.ArtifactsAsync(run.Id)).Contains("report.json"), "Отчёт хранится в отдельном каталоге прогона");
foreach (string benchmark in new[] { "day3", "day5", "day9", "day10" })
{
    var otherRun = await runner.CreateAsync(benchmark, new() { ProviderId = "proxyapi", Branches = false });
    otherRun = await runner.ExecuteAsync(otherRun);
    Check(otherRun.Status == RunStatus.Completed && (await store.ArtifactsAsync(otherRun.Id)).Contains("report.json"),
        $"{benchmark}: типизированный запуск сохраняет отчёт без консольного ввода");
}
var metrics = await provider.GetRequiredService<RunMetricsReader>().ReadAsync(run.Id);
Check(metrics.PromptTokens == 3000 && metrics.CompletionTokens == 300 && metrics.EstimatedRubles == null, "Usage учитывается фактически; неизвестный тариф остаётся Н/д");
await ExpectFailure(() => store.ArtifactPathAsync(run.Id, "../run.json"), "Артефакты не позволяют выйти за каталог запуска");
await ExpectFailure(() => store.GetAsync("../../outside"), "RunId не допускает произвольные пути");

fake.Reset(); fake.Block = true;
var interrupted = await runner.CreateAsync("day4", new() { ProviderId = "ollama", Model = "fake-local" });
using (var cancellation = new CancellationTokenSource())
{
    var pending = runner.ExecuteAsync(interrupted, ct: cancellation.Token);
    await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    cancellation.Cancel();
    var cancelled = await pending;
    Check(cancelled.Status == RunStatus.Cancelled && (await store.GetAsync(cancelled.Id)).Status == RunStatus.Cancelled, "Отмена останавливает вызов и сохраняет состояние");
}
fake.Block = false;

using (var alreadyCancelled = new CancellationTokenSource())
{
    alreadyCancelled.Cancel();
    var beforeStart = await runner.CreateAsync("day4", new() { ProviderId = "ollama", Model = "fake-local" });
    var result = await runner.ExecuteAsync(beforeStart, ct: alreadyCancelled.Token);
    Check(result.Status == RunStatus.Cancelled && result.CompletedCalls == 0, "Отмена до старта не оставляет Running и не вызывает модель");
}

var parallelA = await runner.CreateAsync("day4", new() { ProviderId = "ollama", Model = "model-a" });
var parallelB = await runner.CreateAsync("day4", new() { ProviderId = "ollama", Model = "model-b" });
await Task.WhenAll(runner.ExecuteAsync(parallelA), runner.ExecuteAsync(parallelB));
string logA = await File.ReadAllTextAsync(Path.Combine(store.GetDirectory(parallelA.Id), "output.log"));
string logB = await File.ReadAllTextAsync(Path.Combine(store.GetDirectory(parallelB.Id), "output.log"));
Check(logA.Contains("model-a") && !logA.Contains("model-b") && logB.Contains("model-b") && !logB.Contains("model-a"), "Параллельные исполнители не смешивают вывод и настройки");

var stale = await runner.CreateAsync("day4", new() { ProviderId = "ollama", Model = "fake-local" });
using (var queue = new BenchmarkQueue(runner, store, NullLogger<BenchmarkQueue>.Instance))
{
    await queue.StartAsync(default);
    Check((await store.GetAsync(stale.Id)).Status == RunStatus.Interrupted, "Перезапуск отмечает незавершённый запуск без повторных платных запросов");
    fake.Reset(); fake.Block = true;
    var firstQueued = await queue.SubmitAsync("day4", new() { ProviderId = "ollama", Model = "fake-local" });
    await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var secondQueued = await queue.SubmitAsync("day4", new() { ProviderId = "ollama", Model = "fake-local" });
    await ExpectFailure(() => queue.DeleteAsync(firstQueued.Id), "Активный прогон нельзя удалить через сервис очереди");
    await ExpectFailure(() => queue.DeleteAsync(secondQueued.Id), "Ожидающий прогон нельзя удалить до отмены");
    await queue.CancelAsync(secondQueued.Id);
    await queue.CancelAsync(firstQueued.Id);
    for (int attempt = 0; attempt < 200 && (await store.GetAsync(firstQueued.Id)).Status == RunStatus.Running; attempt++) await Task.Delay(20);
    Check((await store.GetAsync(firstQueued.Id)).Status == RunStatus.Cancelled &&
        (await store.GetAsync(secondQueued.Id)).Status == RunStatus.Cancelled && fake.RequestSizes.Count == 1,
        "Отмена активного и ожидающего прогонов не теряется между очередью и исполнителем");
    fake.Block = false;
    await queue.DeleteAsync(secondQueued.Id);
    Check(!(await store.ListAsync()).Any(r => r.Id == secondQueued.Id), "Отменённый ожидающий прогон удаляется из истории");
    var afterDelete = await queue.SubmitAsync("day4", new() { ProviderId = "ollama", Model = "fake-local" });
    for (int attempt = 0; attempt < 400 && (await store.GetAsync(afterDelete.Id)).Status is RunStatus.Running or RunStatus.Queued; attempt++) await Task.Delay(20);
    Check((await store.GetAsync(afterDelete.Id)).Status == RunStatus.Completed, "Очередь пропускает удалённый элемент и выполняет следующий прогон");
    await queue.DeleteAsync(afterDelete.Id);
    Check(!Directory.Exists(store.GetDirectory(afterDelete.Id)) && Directory.Exists(store.GetDirectory(run.Id)),
        "Удаление убирает журнал/артефакты только выбранного прогона");
    await ExpectFailure(() => queue.DeleteAsync("../outside"), "Удаление отклоняет путь вместо RunId");
    await ExpectFailure(() => queue.DeleteAsync(Guid.NewGuid().ToString("N")), "Удаление отсутствующего прогона возвращает ошибку");
    await queue.StopAsync(default);
}
using (var lease = new StorageLease(provider.GetRequiredService<AppPaths>()))
    await ExpectFailure(() => { using var second = new StorageLease(provider.GetRequiredService<AppPaths>()); return Task.CompletedTask; }, "Два процесса не могут одновременно владеть одним каталогом");

int savedConversations = (await workspace.ListAsync()).Count;
await provider.InitializeUniversalAIAsync();
Check((await workspace.ListAsync()).Count == savedConversations, "Неизменная схема не пересоздаёт рабочую базу");
int savedRuns = (await store.ListAsync()).Count;
await using (var scope = provider.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlRawAsync("UPDATE __UniversalAISchema SET Fingerprint = 'old-schema'");
await provider.InitializeUniversalAIAsync();
Check((await workspace.ListAsync()).Count == 0 && (await store.ListAsync()).Count == savedRuns,
    "Изменение учебной схемы пересоздаёт только БД и сохраняет файлы прогонов");

await Day11Checks.RunAsync(root);
Console.WriteLine($"Все проверки приложения пройдены. Артефакты: {root}");

static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); Console.WriteLine("OK: " + message); }
static async Task ExpectFailure(Func<Task> action, string message) { bool failed = false; try { await action(); } catch { failed = true; } Check(failed, message); }

sealed class FakeFactory(FakeProvider provider) : IProviderFactory { public IAIProvider Create(ProviderProfile profile) => provider; }
sealed class FakeProvider : IAIProvider
{
    private int _active;
    public int MaxConcurrent { get; private set; }
    public List<int> RequestSizes { get; } = [];
    public bool Block { get; set; }
    public bool FailAnswer { get; set; }
    public TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string Name => "Ollama";
    public ProviderCapabilities Capabilities => new() { SupportsFunctionCalling = true };
    public void Reset() { RequestSizes.Clear(); MaxConcurrent = 0; Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    public async Task<ChatResult> SendMessageAsync(ChatRequest request, CancellationToken ct = default)
    {
        int active = Interlocked.Increment(ref _active); MaxConcurrent = Math.Max(MaxConcurrent, active);
        try
        {
            RequestSizes.Add(request.Messages.Count); Entered.TrySetResult();
            if (Block) await Task.Delay(Timeout.Infinite, ct);
            await Task.Delay(15, ct);
            if (FailAnswer && request.Messages[0].Content != UniversalAI.Services.Context.StickyFactsMemory.Instructions)
                throw new HttpRequestException("Fake answer failure");
            string content = request.Messages[0].Content == UniversalAI.Services.Context.StickyFactsMemory.Instructions
                ? "{\"budget\":\"420000\"}" : request.Messages[^1].Content.Contains("ANSWER") ? "ANSWER: 323" : "Принято.";
            return new() { Content = content, FinishReason = "stop", PromptTokens = 100, CompletionTokens = 10, TotalTokens = 110 };
        }
        finally { Interlocked.Decrement(ref _active); }
    }
    public IAsyncEnumerable<string> StreamMessageAsync(ChatRequest request, CancellationToken ct = default) => throw new NotSupportedException();
}
sealed class StubHttpHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization != null) throw new InvalidOperationException("Local request should have no Authorization header.");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"local-ok\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1}}", Encoding.UTF8, "application/json") });
    }
}
