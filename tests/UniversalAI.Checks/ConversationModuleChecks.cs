using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UniversalAI.Configuration;
using UniversalAI.Core;
using UniversalAI.Core.Capabilities;
using UniversalAI.Providers;
using UniversalAI.Services;
using UniversalAI.Services.Context;
using UniversalAI.Services.Invariants;
using UniversalAI.Services.Providers;
using UniversalAI.Services.Tasks;

internal static class ConversationModuleChecks
{
    public static async Task RunAsync(string root)
    {
        string? previous = Environment.GetEnvironmentVariable("UNIVERSALAI_DATA_DIR");
        Environment.SetEnvironmentVariable("UNIVERSALAI_DATA_DIR", Path.Combine(root, "modules"));
        var config = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json")).Build();
        var services = new ServiceCollection().AddLogging().AddUniversalAI(config);
        Environment.SetEnvironmentVariable("UNIVERSALAI_DATA_DIR", previous);
        var fake = new ModuleProvider();
        services.AddSingleton<IProviderFactory>(new Factory(fake));
        await using var sp = services.BuildServiceProvider();
        await sp.InitializeUniversalAIAsync();
        var workspace = sp.GetRequiredService<IWorkspaceClient>();
        var c = await workspace.CreateAsync(new("Модули", "ollama", "fake-local"));
        Check(c.Modules == new ConversationModules() && c.AgentId == "direct", "Новый диалог не подключает задачи, профиль или инварианты");
        var ordinary = await workspace.SendAsync(c.Id, "Привет");
        Check(ordinary.Task == null && fake.Requests.Count == 1 && ordinary.Response.Content == "Обычный ответ",
            "Обычный чат отвечает одним запросом без заполнения задачи или профиля");

        await workspace.UpdateAsync(c.Id, new(Modules: new(Tasks: true), CompressionEnabled: false));
        await workspace.SendAsync(c.Id, "/task start Сохрани мою задачу");
        await workspace.SendAsync(c.Id, "/task plan");
        var planned = (await workspace.GetAsync(c.Id)).TaskState!;
        Check(planned.Plan.Length > 0, "Подключённая задача сохраняет план");
        await workspace.UpdateAsync(c.Id, new(Modules: new()));
        int start = fake.Requests.Count;
        ordinary = await workspace.SendAsync(c.Id, "Теперь просто поговорим");
        Check(ordinary.Task == null && fake.Requests.Count == start + 1 &&
            JsonSerializer.Serialize((await workspace.GetAsync(c.Id)).TaskState) == JsonSerializer.Serialize(planned),
            "Отключённая задача не перехватывает диалог и сохраняет состояние");
        await workspace.UpdateAsync(c.Id, new(Modules: new(Tasks: true)));
        var status = await workspace.SendAsync(c.Id, "/task status");
        Check(status.Task?.After?.Plan == planned.Plan, "Повторное подключение продолжает сохранённую задачу");

        string profile = JsonSerializer.Serialize(new UserProfile { Name = "Анна", Tone = "formal", LearnFromDialogue = false });
        await workspace.UpdateAsync(c.Id, new(Modules: new(Personalization: true), UserProfileJson: profile));
        await workspace.SendAsync(c.Id, "Поговорим с профилем");
        Check(fake.Requests.Last().Messages.Any(m => m.Content.StartsWith("ПРОФИЛЬ ПОЛЬЗОВАТЕЛЯ")), "Подключённый профиль входит в системный контекст");
        await workspace.UpdateAsync(c.Id, new(Modules: new()));
        await workspace.SendAsync(c.Id, "Без профиля");
        Check(!fake.Requests.Last().Messages.Any(m => m.Content.StartsWith("ПРОФИЛЬ ПОЛЬЗОВАТЕЛЯ")) &&
            (await workspace.GetAsync(c.Id)).UserProfileJson == profile, "Отключение профиля убирает его из системного контекста и сохраняет предпочтения");
        await workspace.UpdateAsync(c.Id, new(Modules: new(Tasks: true, Personalization: true)));
        await workspace.SendAsync(c.Id, "/task approve");
        await workspace.SendAsync(c.Id, "/task execute");
        Check(fake.Requests.Last().Messages.Any(m => m.Content.StartsWith("ПРОФИЛЬ ПОЛЬЗОВАТЕЛЯ")), "Задачи и персонализация работают совместно");

        await workspace.UpdateAsync(c.Id, new(Modules: new()));
        await workspace.SendAsync(c.Id, "/invariant set stack = C#");
        c = await workspace.GetAsync(c.Id);
        Check(c.Modules is { Invariants: true, Tasks: false, Personalization: false }, "Явная команда подключает только инварианты");
        var strict = await workspace.SendAsync(c.Id, "Предложи решение");
        Check(strict.Response.Content.Contains("stack = C#"), "Подключённые инварианты проверяют решения");
        await workspace.UpdateAsync(c.Id, new(Modules: new()));
        c = await workspace.GetAsync(c.Id);
        var invariants = InvariantPolicy.Parse(c.InvariantStateJson);
        Check(invariants.Rules["stack"] == "C#" && invariants.Audit.Last() is { Key: "$module.enabled", Before: "True", After: "False" },
            "Явное отключение правил сохраняет значения и фиксируется в audit");
        await workspace.UpdateAsync(c.Id, new(AgentId: "invariants"));
        ordinary = await workspace.SendAsync(c.Id, "Обычное сообщение");
        Check(ordinary.Response.Content == "Обычный ответ", "Старый AgentId не включает явно отключённый модуль");
        var branch = await workspace.BranchAsync(c.Id, "Ветка модулей");
        await workspace.UpdateAsync(branch.Id, new(Modules: new(Invariants: true)));
        Check((await workspace.GetAsync(c.Id)).Modules == new ConversationModules() &&
            (await workspace.GetAsync(branch.Id)).Modules is { Invariants: true }, "Выбор модулей сохраняется между scope и независимо копируется в ветку");
        strict = await workspace.SendAsync(branch.Id, "Решение с сохранённым правилом");
        Check(strict.Response.Content.Contains("stack = C#"), "Повторное подключение использует сохранённые инварианты");

        var legacy = new UniversalAI.Data.Entities.ConversationEntity { TaskStateJson = JsonSerializer.Serialize(planned), UserProfileJson = profile };
        Check(ConversationModules.Resolve(legacy) is { Tasks: true, Personalization: true }, "Прежние JSON-снимки сохраняют активные возможности");
        Check(fake.Requests.All(r => r.MaxTokens == null && r.ReasoningBudgetTokens == null), "Модули не добавляют токеновых лимитов");
    }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); Console.WriteLine("OK: " + message); }
    private sealed class Factory(ModuleProvider provider) : IProviderFactory { public IAIProvider Create(ProviderProfile p) => provider; }
    private sealed class ModuleProvider : IAIProvider
    {
        public string Name => "Modules-offline";
        public ProviderCapabilities Capabilities => new() { SupportsFunctionCalling = true };
        public List<ChatRequest> Requests { get; } = [];
        public Task<ChatResult> SendMessageAsync(ChatRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Requests.Add(request);
            if (request.Messages[0].Content.StartsWith(InvariantPolicy.Instructions))
                return Task.FromResult(new ChatResult { Content = Day14Checks.InvariantProvider.Respond(request.Messages), FinishReason = "stop" });
            if (request.Tools.Any(t => t.Name == "request_task_action") || request.Messages.Any(m => m.Content.Contains("Ты ассистент с конечным автоматом задачи.")))
                return Task.FromResult(Day13Checks.TaskProvider.Respond(request));
            return Task.FromResult(new ChatResult { Content = "Обычный ответ", FinishReason = "stop" });
        }
        public IAsyncEnumerable<string> StreamMessageAsync(ChatRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
