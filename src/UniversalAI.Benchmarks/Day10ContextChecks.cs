using System.Text.Json;
using System.Text.RegularExpressions;
using UniversalAI.Core;
using UniversalAI.Core.Capabilities;
using UniversalAI.Providers;
using UniversalAI.Services.Context;

namespace UniversalAI.Benchmarks;

/// <summary>Deterministic integration checks, never a measurement of real model memory.</summary>
public static class Day10ContextChecks
{
    public static async Task<int> RunAsync()
    {
        try
        {
            List<ChatMessage> history = [];
            for (int i = 0; i < 15; i++) history.AddRange([
                new() { Role = "user", Content = $"message-{i}" }, new() { Role = "assistant", Content = "Принято." }]);
            Check(SlidingWindowMemory.Select(history, 6).SequenceEqual(history.Skip(24)), "Sliding: только последние N сообщений");
            Check(SlidingWindowMemory.Select(history.Take(4).ToList(), 6).Count == 4, "Sliding: короткая история сохраняется");
            Check(SlidingWindowMemory.Select(history, 5).SequenceEqual(history.Skip(25)), "Sliding: нечётное N сохраняет ровно N обычных сообщений");
            var tools = new List<ChatMessage>
            {
                new() { Role = "user", Content = "tool turn" },
                new() { Role = "assistant", ToolCalls = [new() { Id = "t" }] },
                new() { Role = "tool", ToolCallId = "t", Content = "4" },
                new() { Role = "assistant", Content = "4" },
                new() { Role = "user", Content = "next" }, new() { Role = "assistant", Content = "done" }
            };
            Check(SlidingWindowMemory.Select(tools, 4).SequenceEqual(tools.Skip(4)), "Sliding: частичный tool-ход отброшен целиком");
            await ExpectFailure(() => Task.FromResult(SlidingWindowMemory.Select(history, 0)), "Неверный размер окна отклонён");

            FakeProvider fake = new();
            var facts = new Day10ContextSession(new() { Strategy = ContextStrategy.StickyFacts }, fake);
            await facts.AskAsync("Бюджет 480000. Код согласования ЛИПА-47.");
            await facts.AskAsync("Бюджет теперь 420000.");
            Check(facts.State.Facts["budget"] == "420000" && facts.State.Facts["code"] == "ЛИПА-47",
                "Facts: обновление заменяет бюджет и сохраняет остальные ключи");
            Check(fake.Requests[^1].Messages[1].Content.Contains("420000") &&
                  !fake.Requests[^1].Messages[1].Content.Contains("480000"), "Facts обновлены до основного запроса");
            int updates = fake.FactCalls;
            await facts.AskAsync("Какой бюджет?");
            Check(fake.FactCalls == updates + 1 && facts.State.Facts["budget"] == "420000",
                "Facts обрабатываются и на пользовательских вопросах");
            string before = facts.Checkpoint();
            foreach (string failure in new[] { "invalid", "truncated", "exception", "duplicate", "nonstring" })
            {
                fake.Failure = failure;
                await ExpectFailure(async () => { await facts.AskAsync("Бюджет теперь 1."); }, $"Ошибка facts: {failure}");
                Check(facts.Checkpoint() == before, "Ошибка не изменяет память и историю");
            }
            fake.Failure = null;
            using (CancellationTokenSource cancelled = new())
            {
                cancelled.Cancel();
                await ExpectFailure(async () => { await facts.AskAsync("Бюджет 2.", cancellationToken: cancelled.Token); }, "Отмена обновления");
                Check(facts.Checkpoint() == before, "Отмена сохраняет checkpoint");
            }
            var a = Day10ContextSession.Restore(before, fake);
            var b = Day10ContextSession.Restore(before, fake);
            await a.AskAsync("Бюджет теперь 390000.");
            await b.AskAsync("Бюджет теперь 460000.");
            Check(a.State.Facts["budget"] == "390000" && b.State.Facts["budget"] == "460000" && facts.Checkpoint() == before,
                "Checkpoint: две ветки независимы, исходное состояние не меняется");
            a.State.Strategy = ContextStrategy.SlidingWindow;
            var selected = await a.PrepareAsync();
            Check(selected.All(m => m.Role != "system") && selected.Count <= 6, "Переключение на Sliding исключает facts");
            a.State.Strategy = ContextStrategy.StickyFacts;
            Check((await a.PrepareAsync())[0].Content.Contains("390000"), "Переключение обратно восстанавливает свою память");

            var transcript = Day9CompressionScenario.Messages.SelectMany(text => new ChatMessage[]
            {
                new() { Role = "user", Content = text }, new() { Role = "assistant", Content = "Принято." }
            }).ToList();
            Day10ContextBenchmark.ValidateTranscript(transcript);
            await ExpectFailure(() => { Day10ContextBenchmark.ValidateTranscript(history); return Task.CompletedTask; },
                "Посторонний диалог вместо Дня 9 отклонён");
            string output = Path.Combine(Path.GetTempPath(), "UniversalAI-Day10-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(output);
            var recorder = new Day10RecordingProvider(new FakeProvider(), output);
            var report = await Day10ContextBenchmark.ExecuteAsync(recorder, output, "fake-offline", 6, 10, transcript, branches: true);
            Check(report.Complete && report.Strategies.Count == 3 && report.Strategies.All(s => s.Answers.Count == 13 &&
                  s.Branches.Count == 16 && s.BranchIsolationPassed), "Полный pipeline: 3 стратегии, 39 ответов, 48 ответов веток");
            Check(report.Calls.Count(c => c.Scope == "comparison") == 70 &&
                  report.Calls.Count(c => c.Scope == "comparison" && c.Kind == "memory" && c.Strategy == "Summary") == 3 &&
                  report.Calls.Count(c => c.Scope == "comparison" && c.Kind == "memory" && c.Strategy == "StickyFacts") == 28 &&
                  !report.Calls.Any(c => c.Kind == "memory" && c.Strategy == "SlidingWindow"), "Учёт вызовов: comparison=70, summary=3, facts=28, sliding=0");
            Check(report.Calls.Count == 202, "Полный прогон: 202 вызова при N=6, batch=10");
            string markdown = await File.ReadAllTextAsync(Path.Combine(output, "report.md"));
            Check(markdown.Contains("| 1430 | 0 / 13 |") && markdown.Contains("| 4510 | 28 / 13 |") &&
                  markdown.Contains("| 1760 | 3 / 13 |"), "Таблица включает поддержку памяти и исключает расход веток");
            foreach (var strategy in Enum.GetValues<ContextStrategy>())
            {
                var questions = report.Calls.Where(c => c.Scope == "comparison" && c.Kind == "answer" && c.Strategy == strategy.ToString()).ToArray();
                Check(questions.Length == 13 && questions.All(c =>
                    c.Request.Messages.Count(m => m.Role == "assistant") <= (strategy == ContextStrategy.Summary ? 7 : 3)),
                    $"{strategy}: ответы контрольных вопросов не накапливаются");
            }
            var restored = JsonSerializer.Deserialize<Day10Report>(await File.ReadAllTextAsync(Path.Combine(output, "report.json")), Day10ContextBenchmark.Json)!;
            Check(restored.Calls.Count == report.Calls.Count &&
                  (await File.ReadAllLinesAsync(Path.Combine(output, "calls.jsonl"))).Length == report.Calls.Count &&
                  (await File.ReadAllLinesAsync(Path.Combine(output, "answers.csv"))).Length == 88,
                "JSON/JSONL/CSV сохраняют все вызовы и 87 ответов");
            Check(await Day10ContextBenchmark.RunAsync(["--rescore", output]) == 0, "Rescore работает без модели");
            Console.WriteLine($"Все проверки Day-10 пройдены без сети. Тестовые артефакты (подставная модель): {output}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
        Console.WriteLine("OK: " + message);
    }

    private static async Task ExpectFailure(Func<Task> action, string message)
    {
        try { await action(); }
        catch (Exception e) when (e is InvalidOperationException or JsonException or ArgumentException or OperationCanceledException)
        { Console.WriteLine("OK: " + message); return; }
        throw new InvalidOperationException("Ожидалась ошибка: " + message);
    }

    private sealed class FakeProvider : IAIProvider
    {
        public string Name => "FAKE — OFFLINE SELF-TEST, NOT MODEL MEASUREMENT";
        public ProviderCapabilities Capabilities { get; } = new();
        public List<ChatRequest> Requests { get; } = [];
        public int FactCalls { get; private set; }
        public string? Failure { get; set; }

        public Task<ChatResult> SendMessageAsync(ChatRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            string content = "Принято.";
            string finish = "stop";
            if (request.Messages[0].Content == StickyFactsMemory.Instructions)
            {
                FactCalls++;
                if (Failure == "exception") throw new InvalidOperationException("Fake API failure");
                using var payload = JsonDocument.Parse(request.Messages[1].Content);
                var facts = JsonSerializer.Deserialize<Dictionary<string, string>>(payload.RootElement.GetProperty("previous_facts"))!;
                string user = payload.RootElement.GetProperty("user_message").GetString()!;
                var budget = Regex.Match(user, @"[Бб]юджет(?: теперь| —)?\s*(\d+)");
                var code = Regex.Match(user, @"(?:ЛИПА|ИВА)-\d+");
                if (budget.Success) facts["budget"] = budget.Groups[1].Value;
                if (code.Success) facts["code"] = code.Value;
                content = JsonSerializer.Serialize(facts);
                if (Failure == "invalid") content = "not JSON";
                if (Failure == "duplicate") content = "{\"a\":\"1\",\"a\":\"2\"}";
                if (Failure == "nonstring") content = "{\"a\":1}";
                if (Failure == "truncated") finish = "length";
            }
            else if (request.Messages[0].Content.StartsWith("Сожми историю"))
            {
                // Keep a generic identifier only; do not simulate a successful real memory score.
                using var payload = JsonDocument.Parse(request.Messages[1].Content);
                string joined = payload.RootElement.GetProperty("previous_summary").GetString() + " " +
                    string.Join(" ", payload.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("Content").GetString()));
                var code = Regex.Matches(joined, @"(?:ЛИПА|ИВА)-\d+").LastOrDefault();
                content = "Краткая тестовая память. " + code?.Value;
            }
            return Task.FromResult(new ChatResult
            {
                Content = content, FinishReason = finish,
                PromptTokens = 100, CompletionTokens = 10, TotalTokens = 110
            });
        }

        public IAsyncEnumerable<string> StreamMessageAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
