using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UniversalAI.Core;
using UniversalAI.Core.Capabilities;
using UniversalAI.Data;
using UniversalAI.Providers;
using UniversalAI.Services;
using UniversalAI.Services.Agents;
using UniversalAI.Services.Providers;

namespace UniversalAI.Benchmarks;

/// <summary>Offline integration checks: real SQLite, fake model, no paid API calls.</summary>
public static class Day9CompressionChecks
{
    public static async Task<int> RunAsync()
    {
        try
        {
            var teamProbe = Day9CompressionScenario.Probes.Single(p => p.Detail == "Команда");
            var exportProbe = Day9CompressionScenario.Probes.Single(p => p.Detail == "Экспорт");
            Check(Day9CompressionScenario.IsCorrect(teamProbe, "В команде 3 человека.") &&
                  !Day9CompressionScenario.IsCorrect(teamProbe, "13") &&
                  Day9CompressionScenario.IsCorrect(exportProbe, "CSV и UTF-8.") &&
                  !Day9CompressionScenario.IsCorrect(exportProbe, "CSV"),
                "Проверка фактов допускает пояснения, требует оба параметра и различает 3 и 13");
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
            await using AppDbContext db = new(options);
            await DatabaseInitializer.InitializeAsync(db);
            ConversationService conversations = new(db);
            ContextCompressionService compression = new(conversations);
            FakeProvider provider = new();
            int id = await conversations.CreateConversationAsync();
            var conversation = (await conversations.GetConversationAsync(id))!;
            List<ChatMessage> log = [];
            for (int i = 0; i < 15; i++)
            {
                log.Add(new() { Role = "user", Content = $"Деталь {i}: " + new string('я', 400) });
                log.Add(new() { Role = "assistant", Content = "Принято." });
            }

            await conversations.SaveMessagesAsync(id, log.Take(14).ToList());
            var prepared = await compression.PrepareAsync(conversation, log.Take(14).ToList(), provider);
            Check(provider.SummaryCalls == 0 && !prepared.UsesSummary, "До порога API сжатия не вызывается");
            await conversations.SaveMessagesAsync(id, log.Skip(14).Take(2).ToList());
            prepared = await compression.PrepareAsync(conversation, log.Take(16).ToList(), provider);
            Check(provider.SummaryCalls == 1 && prepared.UsesSummary && prepared.CoveredMessages == 10 &&
                  prepared.RecentMessages == 6, "Первое сжатие: 10 старых + 6 исходных сообщений");
            Check(prepared.Messages.Skip(1).SequenceEqual(log.Skip(10).Take(6)), "Последние сообщения переданы без изменений");
            await conversations.SaveMessagesAsync(id, log.Skip(16).Take(10).ToList());
            prepared = await compression.PrepareAsync(conversation, log.Take(26).ToList(), provider);
            using (JsonDocument payload = JsonDocument.Parse(provider.Requests[^1].Messages[1].Content))
            {
                Check(payload.RootElement.GetProperty("previous_summary").GetString() == "Бюджет 420000. Заказчик Марина Соколова." &&
                      payload.RootElement.GetProperty("messages").GetArrayLength() == 10,
                    "Повторное сжатие получает прежнюю память и только новые сообщения");
            }

            await conversations.SaveMessagesAsync(id, log.Skip(26).ToList());
            prepared = await compression.PrepareAsync(conversation, log, provider, force: true);
            Check(prepared.CoveredMessages == 24 && prepared.RecentMessages == 6 && provider.SummaryCalls == 3,
                "Принудительное сжатие обрабатывает остаток меньше batch");
            Check((await conversations.GetMessagesAsync(id)).Count == 30, "Сжатие не удаляет исходный журнал");
            Check((await conversations.GetSummaryAsync(id))!.PromptTokens == 300, "Расход всех обновлений summary накоплен отдельно");
            await using (AppDbContext reopened = new(options))
            {
                ConversationService restored = new(reopened);
                var saved = (await restored.GetConversationAsync(id))!;
                var resumed = await new ContextCompressionService(restored).PrepareAsync(saved,
                    await restored.GetMessagesAsync(id), provider);
                Check(resumed.CoveredMessages == 24 && provider.SummaryCalls == 3,
                    "После открытия нового DbContext память восстановлена без повторного API-вызова");
            }

            await conversations.SetCompressionAsync(id, enabled: false);
            prepared = await compression.PrepareAsync(conversation, log, provider);
            Check(prepared.Messages.SequenceEqual(log) && !prepared.UsesSummary, "Отключение возвращает полную историю");
            await conversations.SetCompressionAsync(id, enabled: true, keep: 8);
            Check((await conversations.GetSummaryAsync(id))!.CoveredMessageCount == 0, "Изменение keep сбрасывает границу памяти");
            prepared = await compression.PrepareAsync(conversation, log, provider);
            Check(prepared.RecentMessages == 8 && prepared.CoveredMessages == 22, "Новая настройка keep применяется");

            string savedSummary = (await conversations.GetSummaryAsync(id))!.Content;
            int savedBoundary = (await conversations.GetSummaryAsync(id))!.CoveredMessageCount;
            provider.FailSummary = true;
            var extended = log.Concat(log.Take(10)).ToList();
            await ExpectFailure(() => compression.PrepareAsync(conversation, extended, provider));
            Check((await conversations.GetSummaryAsync(id))!.Content == savedSummary &&
                  (await conversations.GetSummaryAsync(id))!.CoveredMessageCount == savedBoundary,
                "Ошибка модели не изменяет summary и его границу");
            provider.FailSummary = false;
            provider.TruncateSummary = true;
            await ExpectFailure(() => compression.PrepareAsync(conversation, extended, provider));
            Check((await conversations.GetSummaryAsync(id))!.CoveredMessageCount == savedBoundary,
                "Оборванное summary не заменяет предыдущую память");
            provider.TruncateSummary = false;

            // Tools form a five-message turn. keep=2 must keep the whole final turn.
            int toolsId = await conversations.CreateConversationAsync();
            await conversations.SetCompressionAsync(toolsId, keep: 2, batch: 1);
            var toolsConversation = (await conversations.GetConversationAsync(toolsId))!;
            List<ChatMessage> toolsLog = [];
            for (int i = 0; i < 3; i++)
            {
                toolsLog.Add(new() { Role = "user", Content = new string('ж', 300) });
                toolsLog.Add(new()
                {
                    Role = "assistant", ToolCalls =
                    [
                        new() { Id = $"a{i}", Function = new() { Name = "calculate", Arguments = "{\"expression\":\"2+2\"}" } },
                        new() { Id = $"b{i}", Function = new() { Name = "get_current_time", Arguments = "{}" } }
                    ]
                });
                toolsLog.Add(new() { Role = "tool", ToolCallId = $"a{i}", FunctionName = "calculate", Content = "4" });
                toolsLog.Add(new() { Role = "tool", ToolCallId = $"b{i}", FunctionName = "get_current_time", Content = "10:00" });
                toolsLog.Add(new() { Role = "assistant", Content = "Готово." });
            }

            await conversations.SaveMessagesAsync(toolsId, toolsLog);
            prepared = await compression.PrepareAsync(toolsConversation, toolsLog, provider);
            Check(prepared.CoveredMessages == 10 && prepared.RecentMessages == 5 &&
                  prepared.Messages.Skip(1).SequenceEqual(toolsLog.Skip(10)), "Пары tool_call/result и весь ход остаются вместе");

            // Exercise the actual agent integration, not only context construction.
            FakeResolver resolver = new(provider);
            DirectChatAgent direct = new(new ChatService(conversations, resolver, compression));
            var directTurn = await direct.RunAsync(id, "Проверка памяти");
            Check(directTurn.Context!.UsesSummary && provider.Requests[^1].Messages[1].Content.Contains("Краткая память") &&
                  directTurn.TokenUsage.HistoryEstimateBefore == TokenUsageCalculator.EstimateMessages(log),
                "DirectChatAgent отправляет summary и считает исходную историю отдельно");
            ToolCallingAgent tools = new(conversations, resolver, new AgentToolRegistry(), compression);
            provider.UseTools = true;
            var toolTurn = await tools.RunAsync(toolsId, "Посчитай 2+2");
            Check(toolTurn.Context!.UsesSummary && toolTurn.ToolRounds == 1 &&
                  toolTurn.ModelCalls.Count == 2 && toolTurn.Response.PromptTokens == 200,
                "ToolCallingAgent использует сжатую историю и выполняет цикл инструмента");
            var finalRequest = provider.Requests[^1];
            Check(finalRequest.Messages.Any(m => m.Role == "tool" && m.ToolCallId == "new-call"),
                "Результат нового инструмента присутствует в следующем запросе");

            int shortId = await conversations.CreateConversationAsync();
            var shortConversation = (await conversations.GetConversationAsync(shortId))!;
            var shortLog = log.Take(16).Select(m => new ChatMessage { Role = m.Role, Content = "Да." }).ToList();
            await conversations.SaveMessagesAsync(shortId, shortLog);
            provider.SummaryContent = new string('я', 5000);
            prepared = await compression.PrepareAsync(shortConversation, shortLog, provider);
            int callsAfterVerboseSummary = provider.SummaryCalls;
            Check(!prepared.UsesSummary && prepared.Messages.SequenceEqual(shortLog) &&
                  prepared.EffectiveHistoryTokens == prepared.RawHistoryTokens,
                "Если summary длиннее исходной истории, отправляется исходная история");
            await compression.PrepareAsync(shortConversation, shortLog, provider);
            Check(provider.SummaryCalls == callsAfterVerboseSummary,
                "Повторный запрос не оплачивает то же невыгодное summary ещё раз");
            provider.SummaryContent = "Бюджет 420000. Заказчик Марина Соколова.";

            using CancellationTokenSource cancelled = new();
            cancelled.Cancel();
            try
            {
                await compression.PrepareAsync(conversation, extended, provider, cancelled.Token);
                throw new Exception("Ожидалась отмена.");
            }
            catch (OperationCanceledException)
            {
                Check((await conversations.GetSummaryAsync(id))!.CoveredMessageCount == savedBoundary,
                    "Отмена не продвигает границу summary");
            }

            await conversations.ClearConversationAsync(id);
            Check((await conversations.GetMessagesAsync(id)).Count == 0 && await conversations.GetSummaryAsync(id) == null,
                "Reset удаляет и историю, и summary");
            Console.WriteLine("Все проверки Day-9 пройдены. Сетевые запросы не выполнялись.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        Console.WriteLine("OK: " + name);
    }

    private static async Task ExpectFailure(Func<Task<PreparedContext>> action)
    {
        try { await action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Ожидалась ошибка сжатия.");
    }

    private sealed class FakeResolver(IAIProvider provider) : IProviderResolver
    {
        public Task<IAIProvider> GetProviderForConversationAsync(int id, CancellationToken cancellationToken = default) =>
            Task.FromResult(provider);
    }

    private sealed class FakeProvider : IAIProvider
    {
        public string Name => "Fake";
        public ProviderCapabilities Capabilities { get; } = new() { SupportsFunctionCalling = true };
        public List<ChatRequest> Requests { get; } = [];
        public int SummaryCalls { get; private set; }
        public bool FailSummary { get; set; }
        public bool TruncateSummary { get; set; }
        public bool UseTools { get; set; }
        public string SummaryContent { get; set; } = "Бюджет 420000. Заказчик Марина Соколова.";

        public Task<ChatResult> SendMessageAsync(ChatRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(JsonSerializer.Deserialize<ChatRequest>(JsonSerializer.Serialize(request))!);
            bool summary = request.Messages[0].Content.StartsWith("Сожми историю");
            if (summary && FailSummary) throw new InvalidOperationException("Fake model failure");
            if (summary) SummaryCalls++;
            bool toolCall = !summary && UseTools && request.Tools.Count > 0 && request.Messages[^1].Role == "user";
            return Task.FromResult(new ChatResult
            {
                Content = summary ? SummaryContent : toolCall ? "" : "Готово.",
                FinishReason = summary && TruncateSummary ? "length" : toolCall ? "tool_calls" : "stop",
                PromptTokens = 100, CompletionTokens = 10, TotalTokens = 110,
                ToolCalls = toolCall
                    ? [new() { Id = "new-call", Function = new() { Name = "calculate", Arguments = "{\"expression\":\"2+2\"}" } }]
                    : []
            });
        }

        public IAsyncEnumerable<string> StreamMessageAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
