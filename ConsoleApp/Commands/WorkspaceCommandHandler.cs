using System.Globalization;
using System.Text.Json;
using UniversalAI.Services;

namespace UniversalAI.ConsoleApp.Commands;

/// <summary>Console parsing only; the same use cases are available over HTTP.</summary>
public sealed class WorkspaceCommandHandler(IWorkspaceClient workspace) : IConsoleCommandHandler
{
    public async Task<bool> TryHandleAsync(string input, ConsoleChatSession session, CancellationToken cancellationToken = default)
    {
        if (!input.StartsWith('/')) return false;
        int separator = input.IndexOf(' ');
        string command = (separator < 0 ? input : input[..separator]).ToLowerInvariant();
        string value = separator < 0 ? "" : input[(separator + 1)..].Trim();
        int id = session.ConversationId;
        if (command is "/paste" or "/help" or "/exit") return false;
        try
        {
            ConversationSettings? update = null;
            switch (command)
            {
                case "/new":
                    if (value.Length == 0 && !Console.IsInputRedirected) { Console.Write("Название (Enter — по умолчанию): "); value = Console.ReadLine() ?? ""; }
                    var created = await workspace.CreateAsync(new(value), cancellationToken);
                    session.ConversationId = created.Id; session.AgentId = created.AgentId;
                    Console.WriteLine($"Создан диалог {created.Id}: {created.Title}"); return true;
                case "/history":
                    foreach (var c in await workspace.ListAsync(cancellationToken)) Console.WriteLine($"{c.Id}: {c.Title} [{c.Model}]"); return true;
                case "/open":
                    var opened = await workspace.GetAsync(int.Parse(value), cancellationToken);
                    session.ConversationId = opened.Id; session.AgentId = opened.AgentId;
                    Console.WriteLine($"Открыт {opened.Id}: {opened.Title}"); return true;
                case "/reset": await workspace.ResetAsync(id, cancellationToken); Console.WriteLine("История и память очищены."); return true;
                case "/rename":
                    if (value.Length == 0 && !Console.IsInputRedirected) { Console.Write("Новое название: "); value = Console.ReadLine()?.Trim() ?? ""; }
                    update = new(Title: value); break;
                case "/branch":
                    var branch = await workspace.BranchAsync(id, value, cancellationToken);
                    session.ConversationId = branch.Id; session.AgentId = branch.AgentId;
                    Console.WriteLine($"Создана и открыта ветка {branch.Id}: {branch.Title}"); return true;
                case "/models":
                    foreach (var model in await workspace.ModelsAsync(id, cancellationToken)) Console.WriteLine(model); return true;
                case "/provider":
                    var providers = await workspace.ProvidersAsync(cancellationToken);
                    if (value.Length == 0)
                    {
                        Console.WriteLine($"Текущий: {(await workspace.GetAsync(id, cancellationToken)).ProviderProfileId}");
                        foreach (var p in providers) Console.WriteLine($"{p.Id}: {p.Name}, {p.BaseUrl}; {(p.KeyConfigured ? "готов" : "нужен ключ")}"); return true;
                    }
                    var selected = providers.FirstOrDefault(p => p.Id.Equals(value, StringComparison.OrdinalIgnoreCase) || p.Name.Equals(value, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException("Провайдер не найден. Список: /provider");
                    update = new(ProviderId: selected.Id, ResetGeneration: true); break;
                case "/agent":
                    if (value.Length == 0)
                    {
                        Console.WriteLine($"Текущий агент: {(await workspace.GetAsync(id, cancellationToken)).AgentId}");
                        foreach (var a in await workspace.AgentsAsync(cancellationToken)) Console.WriteLine($"{a.Id}: {a.Name}"); return true;
                    }
                    update = new(AgentId: value); break;
                case "/memory":
                    if (value.Length == 0) { Console.WriteLine((await workspace.GetAsync(id, cancellationToken)).MemoryStrategy); return true; }
                    update = new(MemoryStrategy: value.ToLowerInvariant() switch { "summary" => "Summary", "slidingwindow" or "window" => "SlidingWindow", "stickyfacts" or "facts" => "StickyFacts", "layered" => "Layered", _ => throw new ArgumentException("/memory Summary|SlidingWindow|StickyFacts|Layered") }); break;
                case "/working": update = new(WorkingMemoryJson: value); break;
                case "/profile":
                    if (value.Length == 0) { Console.WriteLine((await workspace.GetAsync(id, cancellationToken)).UserProfileJson); return true; }
                    update = new(UserProfileJson: value == "off" ? "{}" : value); break;
                case "/longterm": update = new(LongTermMemoryJson: value); break;
                case "/forget-layer":
                    update = value.ToLowerInvariant() switch
                    {
                        "short" => new(ClearShortTerm: true),
                        "working" => new(WorkingMemoryJson: "{}"),
                        "long" => new(LongTermMemoryJson: "{}"),
                        _ => throw new ArgumentException("/forget-layer short|working|long")
                    }; break;
                case "/layers":
                    var layers = await workspace.GetAsync(id, cancellationToken);
                    Console.WriteLine($"Диалог: последние {layers.Keep} сообщений; рабочая: {layers.WorkingMemoryJson}; долговременная: {layers.FactsJson}");
                    return true;
                case "/compression":
                    if (value == "now") { await workspace.CompressAsync(id, cancellationToken); Console.WriteLine("Summary обновлено."); return true; }
                    if (value.Length == 0) { var c = await workspace.GetAsync(id, cancellationToken); Console.WriteLine($"{c.MemoryStrategy}: {c.CompressionEnabled}; keep={c.Keep}, batch={c.Batch}"); return true; }
                    string[] parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    update = parts[0] switch
                    {
                        "on" when parts.Length == 1 => new(CompressionEnabled: true),
                        "off" when parts.Length == 1 => new(CompressionEnabled: false),
                        "keep" when parts.Length == 2 => new(Keep: int.Parse(parts[1])),
                        "batch" when parts.Length == 2 => new(Batch: int.Parse(parts[1])),
                        _ => throw new ArgumentException("/compression on|off|keep N|batch N|now")
                    }; break;
                case "/summary": var memory = await workspace.GetAsync(id, cancellationToken); Console.WriteLine(memory.Summary ?? "Summary ещё нет."); Console.WriteLine(memory.FactsJson); return true;
                case "/tokens":
                    var statistics = await workspace.TokensAsync(id, cancellationToken);
                    Console.WriteLine($"Сообщений: {statistics.Answers.MessageCount}; история (оценка): ~{statistics.Answers.EstimatedHistoryTokens} токенов.");
                    Console.WriteLine($"Сохранённый usage ответов: вход {statistics.Answers.PromptTokens?.ToString() ?? "н/д"}, выход {statistics.Answers.CompletionTokens?.ToString() ?? "н/д"}.");
                    Console.WriteLine($"Расчётная стоимость ответов: {statistics.Answers.CostRubles?.ToString("0.00000000", CultureInfo.InvariantCulture) ?? "н/д"} ₽.");
                    Console.WriteLine($"Отдельно поддержка памяти: вход {statistics.MemoryPromptTokens}, выход {statistics.MemoryCompletionTokens}{(statistics.MemoryUsageComplete ? "" : " (usage неполный)")}."); return true;
                case "/settings": Console.WriteLine(JsonSerializer.Serialize(await workspace.GetAsync(id, cancellationToken), new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })); return true;
                case "/defaults": update = new(ResetGeneration: true); break;
                case "/prompt": if (value.Length == 0) { Console.WriteLine((await workspace.GetAsync(id, cancellationToken)).SystemPrompt); return true; } update = new(SystemPrompt: value); break;
                case "/model": if (value.Length == 0) { Console.WriteLine((await workspace.GetAsync(id, cancellationToken)).Model); return true; } update = new(Model: value); break;
                case "/temperature": if (value.Length == 0) { Console.WriteLine((await workspace.GetAsync(id, cancellationToken)).Temperature); return true; } update = new(Temperature: Number(value)); break;
                case "/max_tokens": if (value.Length == 0) { Console.WriteLine((await workspace.GetAsync(id, cancellationToken)).MaxTokens?.ToString() ?? "Без программного лимита"); return true; } update = value == "off" ? new(ClearMaxTokens: true) : new(MaxTokens: int.Parse(value)); break;
                case "/top_p": update = new(TopP: Number(value)); break;
                case "/frequency_penalty": update = new(FrequencyPenalty: Number(value)); break;
                case "/presence_penalty": update = new(PresencePenalty: Number(value)); break;
                case "/seed": update = new(Seed: int.Parse(value)); break;
                case "/reasoning":
                    if (value.Length == 0) { Console.WriteLine((await workspace.GetAsync(id, cancellationToken)).ReasoningEffort ?? "off"); return true; }
                    update = value == "off" ? new(ClearReasoning: true) : new(ReasoningEffort: value); break;
                case "/reasoning_budget": update = new(ReasoningBudgetTokens: int.Parse(value)); break;
                default: Console.WriteLine("Неизвестная команда. Справка: /help"); return true;
            }
            var updated = await workspace.UpdateAsync(id, update!, cancellationToken);
            session.AgentId = updated.AgentId;
            Console.WriteLine("Настройки сохранены.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception e) { Console.WriteLine($"Ошибка: {e.Message}"); }
        return true;
    }
    private static double Number(string value) => double.Parse(value.Replace(',', '.'), CultureInfo.InvariantCulture);
}
