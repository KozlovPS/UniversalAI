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
using UniversalAI.Web.Components;

internal static class Day12Checks
{
    public static async Task RunAsync(string root)
    {
        string? previous = Environment.GetEnvironmentVariable("UNIVERSALAI_DATA_DIR");
        Environment.SetEnvironmentVariable("UNIVERSALAI_DATA_DIR", Path.Combine(root, "day12"));
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json")).Build();
        var fake = new ProfileProvider();
        var services = new ServiceCollection().AddLogging().AddUniversalAI(configuration).AddUniversalAIBenchmarks();
        Environment.SetEnvironmentVariable("UNIVERSALAI_DATA_DIR", previous);
        services.AddSingleton<IProviderFactory>(new Factory(fake));
        await using var sp = services.BuildServiceProvider();
        await sp.InitializeUniversalAIAsync();
        var workspace = sp.GetRequiredService<IWorkspaceClient>();
        var chat = await workspace.CreateAsync(new("Профиль", "ollama", "fake-local"));
        Check(chat.MaxTokens == null, "Новый чат не задаёт программный лимит ответа");
        var profile = new UserProfile { Name = "Анна" };
        await workspace.UpdateAsync(chat.Id, new(MemoryStrategy: "Layered", UserProfileJson: JsonSerializer.Serialize(profile),
            WorkingMemoryJson: "{\"task\":\"one\"}", LongTermMemoryJson: "{\"fact\":\"two\"}"));
        var first = await workspace.SendAsync(chat.Id, Day12PersonalizationBenchmark.LearningText);
        var learned = UserProfileMemory.Parse((await workspace.GetAsync(chat.Id)).UserProfileJson);
        Check(learned.Length == "brief" && learned.Code == "forbidden" && learned.Tone == "informal" && learned.Evidence.Count == 3 &&
            first.Context is { ProfileTokens: > 0, ProfileCall: not null }, "Профиль пополняется из реплики, применяется уже к текущему ответу и сохраняет источники");
        Check((await workspace.TokensAsync(chat.Id)).MemoryPromptTokens == 100, "Usage извлечения профиля учтён отдельно от ответа");
        var clone = await workspace.BranchAsync(chat.Id, "Ветка профиля");
        foreach (string strategy in new[] { "Layered", "SlidingWindow", "StickyFacts", "Summary" })
        {
            await workspace.UpdateAsync(chat.Id, new(MemoryStrategy: strategy, CompressionEnabled: false, AgentId: "tools", ClearShortTerm: true));
            int start = fake.Requests.Count;
            var turn = await workspace.SendAsync(chat.Id, "Узнай время");
            Check(turn.ToolRounds == 1 && fake.Requests.Skip(start).Where(r => r.Messages[0].Content != UserProfileMemory.Instructions)
                .All(r => r.Messages.Any(m => m.Content.StartsWith("ПРОФИЛЬ ПОЛЬЗОВАТЕЛЯ"))),
                $"{strategy}: профиль в каждом запросе tools-цикла даже при выключенной памяти");
        }
        Check(UserProfileMemory.Parse((await workspace.GetAsync(chat.Id)).UserProfileJson).Tone == "informal", "Очистка истории сохраняет профиль");
        await workspace.UpdateAsync(chat.Id, new(AgentId: "direct", CompressionEnabled: true, MemoryStrategy: "Layered", Keep: 2));
        for (int i = 0; i < 6; i++) await workspace.SendAsync(chat.Id, "Обычный вопрос " + i);
        Check(fake.Requests.Last().Messages.Count(m => m.Content.StartsWith("ПРОФИЛЬ ПОЛЬЗОВАТЕЛЯ")) == 1 &&
            fake.Requests.Last().Messages.Count < 10, "Профиль не теряется и не дублируется после сокращения истории");
        await workspace.SendAsync(chat.Id, "Теперь предпочитаю подробные ответы");
        Check(UserProfileMemory.Parse((await workspace.GetAsync(chat.Id)).UserProfileJson).Length == "detailed" &&
            UserProfileMemory.Parse((await workspace.GetAsync(clone.Id)).UserProfileJson).Length == "brief", "Пополнение профиля ветки независимо");
        var beforeFailure = await workspace.GetAsync(chat.Id);
        int messages = (await workspace.MessagesAsync(chat.Id)).Total;
        fake.FailAt = fake.Requests.Count + 2;
        bool failed = false;
        try { await workspace.SendAsync(chat.Id, Day12PersonalizationBenchmark.LearningText); }
        catch (HttpRequestException) { failed = true; }
        Check(failed && (await workspace.GetAsync(chat.Id)).UserProfileJson == beforeFailure.UserProfileJson &&
            (await workspace.MessagesAsync(chat.Id)).Total == messages, "Ошибка основного ответа не сохраняет извлечённый профиль и половину хода");
        fake.FailAt = null;
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await workspace.SendAsync(chat.Id, "Вопрос", cancelled.Token); } catch (OperationCanceledException) { }
        }
        Check((await workspace.GetAsync(chat.Id)).UserProfileJson == beforeFailure.UserProfileJson, "Отмена не меняет профиль");
        foreach (string invalid in new[] { "[]", "null", "{\"Tone\":\"rude\"}", "{\"Name\":null}" })
        {
            failed = false;
            try { await workspace.UpdateAsync(chat.Id, new(Title: "не менять", UserProfileJson: invalid)); } catch (ArgumentException) { failed = true; }
            Check(failed && (await workspace.GetAsync(chat.Id)).Title == beforeFailure.Title, "Некорректный профиль отклоняется до изменения данных");
        }
        failed = false;
        try { UserProfileMemory.ApplyPatch(profile, "Вопрос", """{"Length":{"value":"brief","evidence":"придуманная цитата"}}"""); }
        catch (InvalidOperationException) { failed = true; }
        Check(failed, "Извлечение без точной цитаты отклоняется");
        Check(UserProfileMemory.ApplyPatch(profile, "Общайся на ты", """{"Constraints":"на ты"}""").Constraints == "на ты",
            "Провайдер может вернуть ограничение строкой только при точном совпадении с репликой");
        Check(JsonSerializer.Serialize(UserProfileMemory.ApplyPatch(learned, "Обычный вопрос", "{}\n\n```json\n{}\n```")) == JsonSerializer.Serialize(learned),
            "Точный ответ сбойного прогона: одинаковый повтор JSON в Markdown не ломает обновление");
        const string validPatch = """{"Length":{"value":"brief","evidence":"кратко"}}""";
        Check(UserProfileMemory.ApplyPatch(profile, "Ответь кратко", "```json\r\n" + validPatch + "\r\n```").Length == "brief" &&
            UserProfileMemory.ApplyPatch(profile, "Ответь кратко", validPatch + "\n```json\n" + validPatch + "\n```").Length == "brief",
            "Markdown-обёртка и одинаковый повтор непустого профиля сохраняют проверку цитат");
        Check(UserProfileMemory.ApplyPatch(profile, "Не используй ``` в ответе", """{"Constraints":"```"}""").Constraints == "```",
            "Обратные кавычки внутри JSON-строки не удаляются");
        const string explanatoryResponse = "{}\n\n**Проверка:** в реплике «Что такое транзакция?» нет явно названных предпочтений о стиле ответов — только сам вопрос по теме. Поэтому возвращён пустой объект `{}`: выводить Length/Code/Tone/Constraints было бы домыслом.";
        string? responseNotice = null;
        Check(JsonSerializer.Serialize(UserProfileMemory.ApplyPatch(learned, "Что такое транзакция?", explanatoryResponse, notice => responseNotice = notice)) == JsonSerializer.Serialize(learned) &&
            responseNotice != null, "Точный ответ v5: пояснение после JSON не меняет профиль, нормализация явно отмечена");
        foreach (string formatted in new[] { validPatch + "\n\nПроверка: поле извлечено из реплики.",
            "```json\n" + validPatch + "\n```\nПояснение: это явное предпочтение.",
            validPatch + "\n" + validPatch, "```json\n" + validPatch + "\n" + validPatch + "\n```" })
            Check(UserProfileMemory.ApplyPatch(profile, "кратко", formatted).Length == "brief",
                "Пояснение и равнозначные повторы в разных обёртках не теряют извлечённое предпочтение");
        foreach (string invalid in new[] { "{}\n```json\n" + validPatch + "\n```", "{}\n" + validPatch,
            "{}\nПояснение. Пример: " + validPatch, "{}\nПояснение: [{\"Tone\":{\"value\":\"formal\",\"evidence\":\"кратко\"}}]",
            "Пояснение перед объектом {}", "```json\n{}", "```javascript\n{}\n```" })
        {
            failed = false;
            try { UserProfileMemory.ApplyPatch(learned, "кратко", invalid); }
            catch (Exception e) when (e is InvalidOperationException or JsonException) { failed = true; }
            Check(failed, "Противоречащие объекты и некорректное начало/блоки JSON отклоняются");
        }
        foreach (string invalid in new[] { "{", "Пояснение перед JSON {}", "{}\n" + validPatch,
            """{"Length":{"value":"brief","evidence":"выдуманная цитата"}}""" })
        {
            string? skipped = null;
            var preserved = await UserProfileMemory.LearnAsync(learned, "кратко", "fake-local",
                new ProfileProvider { ForcedProfileResponse = invalid }, default, onResponseNotice: notice => skipped = notice);
            Check(ReferenceEquals(preserved, learned) && skipped?.StartsWith("Обновление профиля пропущено") == true,
                "Некорректное/неоднозначное извлечение сохраняет прежний профиль и явное предупреждение");
        }
        await workspace.UpdateAsync(chat.Id, new(UserProfileJson: JsonSerializer.Serialize(learned with { LearnFromDialogue = false })));
        int calls = fake.Requests.Count;
        await workspace.SendAsync(chat.Id, "Теперь предпочитаю подробные ответы");
        Check(fake.Requests.Count == calls + 1 && UserProfileMemory.Parse((await workspace.GetAsync(chat.Id)).UserProfileJson).Length == "brief", "Можно отключить обучение, сохранив применение профиля");
        await workspace.ResetAsync(chat.Id);
        Check((await workspace.GetAsync(chat.Id)).UserProfileJson == "{}", "Полный сброс удаляет профиль");
        Check(!Day12PersonalizationBenchmark.Score(Day12PersonalizationBenchmark.LearnedExpectation(), "Вы можете использовать `code`.").Passed,
            "Scorer отклоняет неправильные тон и код независимо от извлечённого профиля");

        var runner = sp.GetRequiredService<BenchmarkRunner>();
        var store = sp.GetRequiredService<FileRunStore>();
        fake.Requests.Clear();
        fake.WrapProfileResponses = true;
        var run = await runner.ExecuteAsync(await runner.CreateAsync("day12", new() { ProviderId = "ollama", Model = "fake-local" }));
        var report = JsonSerializer.Deserialize<Day12PersonalizationBenchmark.Report>(await File.ReadAllTextAsync(await store.ArtifactPathAsync(run.Id, "report.json")), FileRunStore.Json)!;
        Check(run.Status == RunStatus.Completed && run.CompletedCalls == 25 && report.Completed && report.Observations.Count == 13 &&
            report.Observations.All(o => o.Assessment.Passed) && report.StabilityAnswered == 10 && report.FirstViolation == null,
            "Day12 offline: 25 вызовов, 3 сравнения, 10 последовательных ответов, 39 оценок");
        Check(report.Snapshots.Count(s => s.ResponseNotice != null) == 12 &&
            (await File.ReadAllTextAsync(await store.ArtifactPathAsync(run.Id, "report.md"))).Contains("Примечание формата") &&
            (await File.ReadAllTextAsync(Path.Combine(store.GetDirectory(run.Id), "output.log"))).Contains("Формат ответа извлечения нормализован"),
            "Нормализация и пропуск некорректных memory-обновлений сохранены в снимках, отчёте и журнале");
        Check(report.Snapshots.Any(s => s.ResponseNotice?.StartsWith("Обновление профиля пропущено") == true),
            "Полный pipeline доходит до 10-го ответа даже при одиночной открывающей скобке в извлечении");
        Check(fake.Requests.All(r => r.MaxTokens == null && r.ReasoningBudgetTokens == null), "Day12: ни один запрос не ограничивает токены программно");
        Check(report.Observations.Take(3).All(o => o.Question == Day12PersonalizationBenchmark.Question) &&
            report.Snapshots.Last().PromptMessages.Count < 12, "Общий вопрос одинаков; устойчивость проверяется с сокращением истории");
        var view = await ExperimentViewData.LoadAsync(store, run, await store.ArtifactsAsync(run.Id), default);
        var documents = await ExperimentDocuments.LoadAsync(store, run, sp.GetRequiredService<BenchmarkRegistry>().Get("day12").Definition,
            await store.ArtifactsAsync(run.Id), view, default);
        Check(view.Presentation?.Stages.Count == 5 && view.Calls.Count(c => c.Passed == true) == 13 &&
            view.Calls.Count(c => c.Kind == "memory") == 12 && documents.Outcomes.Count == 4 && documents.Materials.Assignment?.Contains("10 вопросов") == true,
            "Веб-разбор: этапы, оценки, memory-вызовы, отчёт, исходное задание и материалы");
        fake.FailAt = fake.Requests.Count + 4;
        var partial = await runner.ExecuteAsync(await runner.CreateAsync("day12", new() { ProviderId = "ollama", Model = "fake-local" }));
        var partialReport = JsonSerializer.Deserialize<Day12PersonalizationBenchmark.Report>(await File.ReadAllTextAsync(await store.ArtifactPathAsync(partial.Id, "report.json")), FileRunStore.Json)!;
        Check(partial.Status == RunStatus.Failed && !partialReport.Completed && partialReport.Observations.Count == 2 && partialReport.Error != null,
            "Day12: ошибка API сохраняет частичный отчёт и снимки");
        fake.FailAt = null;
        using (var cancellation = new CancellationTokenSource())
        {
            int cancelAt = fake.Requests.Count + 4;
            fake.OnCall = number => { if (number == cancelAt) cancellation.Cancel(); };
            var cancelled = await runner.ExecuteAsync(await runner.CreateAsync("day12", new() { ProviderId = "ollama", Model = "fake-local" }), ct: cancellation.Token);
            var cancelledReport = JsonSerializer.Deserialize<Day12PersonalizationBenchmark.Report>(await File.ReadAllTextAsync(await store.ArtifactPathAsync(cancelled.Id, "report.json")), FileRunStore.Json)!;
            Check(cancelled.Status == RunStatus.Cancelled && !cancelledReport.Completed && cancelledReport.Observations.Count == 2,
                "Day12: отмена во время извлечения сохраняет частичный отчёт и статус Cancelled");
            fake.OnCall = null;
        }
        fake.IgnoreLearning = true;
        var missed = await runner.ExecuteAsync(await runner.CreateAsync("day12", new() { ProviderId = "ollama", Model = "fake-local" }));
        var missedReport = JsonSerializer.Deserialize<Day12PersonalizationBenchmark.Report>(await File.ReadAllTextAsync(await store.ArtifactPathAsync(missed.Id, "report.json")), FileRunStore.Json)!;
        Check(missedReport.FirstViolation == 1 && !missedReport.Observations[2].Assessment.Passed, "Неудачное извлечение не подменяет эталон и не даёт ложного успешного результата");
        Console.WriteLine($"Day12 offline RunId: {run.Id}; артефакты: {store.GetDirectory(run.Id)}");
    }

    private static void Check(bool ok, string text) { if (!ok) throw new InvalidOperationException(text); Console.WriteLine("OK: " + text); }
    private sealed class Factory(ProfileProvider provider) : IProviderFactory { public IAIProvider Create(ProviderProfile profile) => provider; }
    internal sealed class ProfileProvider : IAIProvider
    {
        public string Name => "Profile-offline";
        public ProviderCapabilities Capabilities => new() { SupportsFunctionCalling = true };
        public List<ChatRequest> Requests { get; } = [];
        public int? FailAt { get; set; }
        public bool IgnoreLearning { get; set; }
        public Action<int>? OnCall { get; set; }
        public bool WrapProfileResponses { get; set; }
        public string? ForcedProfileResponse { get; init; }
        public Task<ChatResult> SendMessageAsync(ChatRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Requests.Add(request);
            OnCall?.Invoke(Requests.Count); ct.ThrowIfCancellationRequested();
            if (Requests.Count == FailAt) throw new HttpRequestException("Offline API failure");
            var response = Respond(request, IgnoreLearning);
            if (ForcedProfileResponse != null && request.Messages[0].Content == UserProfileMemory.Instructions)
                response.Content = ForcedProfileResponse;
            if (WrapProfileResponses && request.Messages[0].Content == UserProfileMemory.Instructions)
                response.Content = (Requests.Count % 3) switch
                {
                    0 => "{",
                    1 => response.Content + "\n\n```json\n" + response.Content + "\n```",
                    _ => "```json\n" + response.Content + "\n```\nПояснение: это результат извлечения."
                };
            return Task.FromResult(response);
        }
        public static ChatResult Respond(ChatRequest request, bool ignoreLearning = false)
        {
            string content;
            if (request.Messages[0].Content == UserProfileMemory.Instructions)
            {
                string text = JsonDocument.Parse(request.Messages.Last().Content).RootElement.GetProperty("utterance").GetString()!;
                var patch = new Dictionary<string, object>();
                if (!ignoreLearning && text == Day12PersonalizationBenchmark.LearningText)
                {
                    patch["Length"] = new { value = "brief", evidence = text };
                    patch["Code"] = new { value = "forbidden", evidence = text };
                    patch["Tone"] = new { value = "informal", evidence = text };
                }
                if (!ignoreLearning && text == "Теперь предпочитаю подробные ответы") patch["Length"] = new { value = "detailed", evidence = text };
                content = JsonSerializer.Serialize(patch);
            }
            else if (request.Tools.Count > 0 && request.Messages.Last().Role == "user" && request.Messages.Last().Content == "Узнай время")
                return new() { FinishReason = "tool_calls", ToolCalls = [new() { Id = "time-1", Function = new() { Name = "get_current_time", Arguments = "{}" } }] };
            else
            {
                string profile = request.Messages.FirstOrDefault(m => m.Content.StartsWith("ПРОФИЛЬ ПОЛЬЗОВАТЕЛЯ"))?.Content ?? "";
                content = profile.Contains("Тон: неформально") ? "Ты можешь использовать этот подход." : "Вы можете использовать этот подход.";
                if (profile.Contains("Объём: подробно")) content += " " + string.Join(" ", Enumerable.Repeat("Подробное объяснение работы алгоритма и примеры применения.", 28));
                if (profile.Contains("Формат: включай пример кода")) content += "\n```csharp\nvar result = 42;\n```";
            }
            return new() { Content = content, FinishReason = "stop", PromptTokens = 100, CompletionTokens = 30, TotalTokens = 130 };
        }
        public IAsyncEnumerable<string> StreamMessageAsync(ChatRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
