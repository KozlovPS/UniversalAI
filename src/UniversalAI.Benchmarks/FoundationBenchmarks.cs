using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UniversalAI.Benchmarks.Runtime;
using UniversalAI.Configuration;
using UniversalAI.Core;
using UniversalAI.Core.Capabilities;
using UniversalAI.Data;
using UniversalAI.Providers;
using UniversalAI.Services;
using UniversalAI.Services.Agents;
using UniversalAI.Services.Providers;

namespace UniversalAI.Benchmarks;

/// <summary>Days 1/2/6/7/8 use the production HTTP and agent/storage paths, with independent run data.</summary>
public static class FoundationBenchmarks
{
    public const string ScenarioVersion = "1", ScorerVersion = "1";
    public const string FormatQuestion = "Назови три цвета светофора и объясни назначение каждого.";
    public static readonly string[] FormatModes = ["baseline", "format", "limit", "stop", "schema", "combined"];
    public static JsonElement ResponseSchema => JsonSerializer.SerializeToElement(new { type = "json_schema", json_schema = new {
        name = "traffic_lights", strict = true, schema = new { type = "object", additionalProperties = false,
            properties = new { colors = new { type = "array", items = new { type = "object", additionalProperties = false,
                properties = new { color = new { type = "string" }, meaning = new { type = "string" } }, required = new[] { "color", "meaning" } } } },
            required = new[] { "colors" } } } });
    public sealed record Observation(int Number, string Group, string Input, string Expected, string Answer,
        bool Passed, List<int> CallNumbers, int EstimatedRequestTokens, int HistoryTokens,
        int? PromptTokens, int? CompletionTokens, bool? Parseable = null, bool? SchemaValid = null, string? Error = null)
    {
        public int EstimatedResponseTokens { get; init; }
        public decimal? CostRubles { get; init; }
        public decimal? CumulativeCostRubles { get; init; }
    }
    public sealed record Report(int Day, bool Completed, string? Error, List<Observation> Observations,
        Dictionary<string, string> Evidence, string Conclusion);

    public static async Task<int> ExecuteAsync(int day, BenchmarkSettings settings, BenchmarkExecutionContext context)
    {
        var provider = new ObservedProvider(context.CreateProvider());
        List<Observation> observations = [];
        Dictionary<string, string> evidence = [];
        var presentation = new ExperimentPresentation();
        bool completed = false;
        string? error = null;
        string conclusion = "Измерение пока не завершено.";
        Directory.CreateDirectory(context.Directory);
        context.Output.WriteLine($"ДЕНЬ {day}: {CourseAssignments.Find("day" + day)!.Title}\nМодель: {settings.Model}. Источник задания: полный DAYS.md.\n" +
            (day == 2 ? "6 режимов × 3 повтора = 18 независимых запросов; max_tokens=16 только в limit/combined, как требует задание дня 2. Stop=КОНЕЦ, JSON Schema strict=true."
            : day == 6 ? "Три независимых запроса: без инструментов, один инструмент, два зависимых инструмента подряд. Цикл выполняет production ToolCallingAgent."
            : day == 7 ? "Диалог → инструмент → полное закрытие SQLite/агента → новый runtime → продолжение из SQLite с вызовами и результатами инструментов."
            : day == 8 ? $"Короткий и длинный диалоги, затем реальная попытка переполнения. Заявленное окно модели: {settings.ContextWindowTokens}; его нужно сверить с выбранной моделью. Локальные оценки отделены от usage API; ограничений ответа нет."
            : "Один реальный запрос → ответ → вывод в интерфейсе. Ограничений токенов нет.") +
            "\nСохраняются: запросы/ответы/usage, output.log, report.json/md, experiment-view.json; все операции изолированы в RunId.");
        await SaveAsync();
        try
        {
            switch (day)
            {
                case 1:
                    await RequestAsync("api", "Сколько будет 2 + 2? Ответь числом.", "4", [], r => new() { Model = settings.Model!, Messages = [new() { Role = "user", Content = r }] }, answer => answer.Trim().Trim('.') == "4");
                    conclusion = "Код отправил запрос через API и сохранил фактический ответ; правильность проверена отдельно от доступности API.";
                    break;
                case 2:
                    await FormatsAsync();
                    conclusion = string.Join(" ", observations.GroupBy(o => o.Group).Select(g => $"{g.Key}: JSON {g.Count(o => o.Parseable == true)}/{g.Count()}, схема {g.Count(o => o.SchemaValid == true)}/{g.Count()}, ошибок {g.Count(o => o.Error != null)}.")) +
                        " Явный формат не гарантирует парсинг; лимит/stop могут оборвать JSON. Строгая схема проверяется кодом; несовместимость endpoint сохраняется как ошибка, не подменяется промптом.";
                    break;
                case 6:
                    await ToolsAsync();
                    conclusion = "Проверены три режима вызовов. Два зависимых инструмента должны выполняться в разных раундах; запросы и результаты сохранены целиком.";
                    break;
                case 7:
                    await PersistenceAsync();
                    conclusion = "Агент и SQLite-контекст закрыты и созданы заново; после восстановления используются исходные сообщения и результаты инструмента. Проверяется восстановление runtime, внешний перезапуск процесса дополнительно проверяет веб-тест.";
                    break;
                case 8:
                    await TokensAsync();
                    conclusion = "Рост входа/истории и usage API показан по ходам. Переполнение подтверждается только фактической ошибкой контекста провайдера; успешный большой запрос не выдаётся за превышение лимита.";
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(day));
            }
            completed = true;
            await SaveAsync();
        }
        catch (Exception e) { error = e.Message; await SaveAsync(); throw; }
        context.Output.WriteLine($"ИТОГ: {observations.Count(o => o.Passed)}/{observations.Count} проверок.\n{conclusion}");
        return 0;

        async Task FormatsAsync()
        {
            for (int repeat = 1; repeat <= 3; repeat++)
            foreach (string mode in FormatModes)
            {
                string instructions = "Верни только JSON: {\"colors\":[{\"color\":\"...\",\"meaning\":\"...\"}]}; три объекта цветов, без Markdown и лишних полей.";
                var request = new ChatRequest { Model = settings.Model!, Messages = [new() { Role = "user", Content = FormatQuestion }] };
                if (mode != "baseline") request.Messages.Insert(0, new() { Role = "system", Content = instructions +
                    (mode is "stop" or "combined" ? " После JSON напиши КОНЕЦ, затем пояснение, которое должно быть отсечено stop sequence." : "") });
                if (mode is "limit" or "combined") request.MaxTokens = 16;
                if (mode is "stop" or "combined") request.Stop = ["КОНЕЦ"];
                if (mode is "schema" or "combined") request.ResponseFormat = ResponseSchema;
                await RequestAsync(mode, FormatQuestion, "JSON со строго проверяемой схемой; baseline и лимиты могут её нарушать", [], _ => request,
                    answer => ValidateFormat(answer).Schema, captureFormat: true, tolerateError: true);
                evidence[$"{mode}/{repeat}"] = JsonSerializer.Serialize(new { request.MaxTokens, request.Stop, request.ResponseFormat });
            }
        }

        async Task ToolsAsync()
        {
            await using var db = await OpenDatabaseAsync("tools.db");
            var service = new ConversationService(db);
            foreach (var probe in new (string Group, string Input, string Expected, int Rounds)[] {
                (Group: "no-tools", Input: "Ответь ровно ПРИВЕТ. Инструменты не нужны.", Expected: "ПРИВЕТ", Rounds: 0),
                ("one-tool", "Вычисли 17 * 19 с помощью calculate. Верни только итоговое число.", "323", 1),
                ("two-tools", "Сначала брось один шестигранный кубик через roll_dice. Затем передай фактическое выпавшее число в calculate и умножь его на 7. Не угадывай бросок. Верни только произведение.", "Фактический бросок × 7", 2) })
            {
                int id = await CreateConversationAsync(service);
                int first = provider.Calls.Count;
                var agent = Agent(service);
                context.Output.WriteLine($"ЭТАП {probe.Group}: {probe.Input}\nОЖИДАНИЕ: {probe.Expected}; раундов {probe.Rounds}");
                var turn = await agent.RunAsync(id, probe.Input, context.CancellationToken);
                var history = await service.GetMessagesAsync(id, context.CancellationToken);
                var tools = history.Where(m => m.Role == "tool").ToList();
                bool passed = probe.Rounds == 0 ? turn.ToolRounds == 0 && turn.Response.Content.Trim().Trim('.') == "ПРИВЕТ"
                    : probe.Rounds == 1 ? turn.ToolRounds == 1 && tools.Count == 1 && tools[0].FunctionName == "calculate" && turn.Response.Content.Contains("323")
                    : VerifyDependentTools(history, turn.Response.Content) && turn.ToolRounds >= 2;
                await RecordAsync(probe.Group, probe.Input, probe.Expected, turn.Response.Content, passed, first, history);
                await WriteFile($"{probe.Group}-history.json", JsonSerializer.Serialize(history, FileRunStore.Json));
            }
        }

        async Task PersistenceAsync()
        {
            int id;
            string before;
            await using (var db = await OpenDatabaseAsync("memory.db"))
            {
                var service = new ConversationService(db);
                id = await CreateConversationAsync(service);
                int first = provider.Calls.Count;
                var greeting = await Agent(service).RunAsync(id, "Запомни код проекта МАЯК-731. Подтверди коротко.", context.CancellationToken);
                await RecordAsync("dialogue", "Код проекта МАЯК-731", "Сообщение сохранено", greeting.Response.Content, true, first, await service.GetMessagesAsync(id));
                first = provider.Calls.Count;
                var turn = await Agent(service).RunAsync(id, "Вычисли 17 * 19 через calculate. Верни только число.", context.CancellationToken);
                var history = await service.GetMessagesAsync(id);
                before = JsonSerializer.Serialize(history);
                await WriteFile("history-before-restart.json", before);
                await RecordAsync("before-restart", "17 * 19 через calculate", "323 и сохранённые tool call/result", turn.Response.Content,
                    history.Any(m => m.Role == "tool" && m.Content.Contains("323")) && history.Any(m => m.ToolCalls?.Count > 0), first, history);
            }
            // Both the tracked entities and agent are gone. New context reads only the on-disk database.
            await using (var db = await OpenDatabaseAsync("memory.db"))
            {
                var service = new ConversationService(db);
                var restored = await service.GetMessagesAsync(id);
                bool exact = before == JsonSerializer.Serialize(restored);
                evidence["restoredHistoryExactly"] = exact.ToString();
                int first = provider.Calls.Count;
                var turn = await Agent(service).RunAsync(id, "Какой код проекта я называл и какой результат calculate был до перезапуска? Инструменты повторно не вызывай. Верни код и число.", context.CancellationToken);
                bool replayed = provider.Calls.Skip(first).Any(c => c.Request.Messages.Any(m => m.Role == "tool" && m.Content.Contains("323")));
                await RecordAsync("after-restart", "Код и прежний результат инструмента", "МАЯК-731 и 323 без повторного вызова инструмента", turn.Response.Content,
                    exact && replayed && turn.ToolRounds == 0 && turn.Response.Content.Contains("МАЯК-731") && turn.Response.Content.Contains("323"), first, restored);
                await WriteFile("history-after-restart.json", JsonSerializer.Serialize(await service.GetMessagesAsync(id), FileRunStore.Json));
            }
        }

        async Task TokensAsync()
        {
            if (settings.ContextWindowTokens is < 1024 or > 2000000) throw new ArgumentException("Укажите реальный размер окна выбранной модели от 1024 до 2000000 через --context-window.");
            List<ChatMessage> history = [];
            for (int turn = 0; turn < 6; turn++)
            {
                string input = turn == 0 ? "Сколько будет 2+2? Ответь числом." : $"Шаг {turn}. Данные:\n" + string.Concat(Enumerable.Repeat("Факт: проект использует SQLite, бюджет 731 рублей.\n", 80)) + "\nПодтверди одним словом ПРИНЯТО.";
                var answer = await RequestAsync(turn == 0 ? "short" : "long", input, turn == 0 ? "4" : "ПРИНЯТО", history,
                    text => new() { Model = settings.Model!, Messages = [.. history, new() { Role = "user", Content = text }] }, _ => true);
                history.Add(new() { Role = "user", Content = input });
                history.Add(new() { Role = "assistant", Content = answer });
            }
            evidence["declaredContextWindowTokens"] = settings.ContextWindowTokens.ToString();
            string overflow = string.Concat(Enumerable.Repeat("x ", checked(settings.ContextWindowTokens + 8192))) + "\nОтветь одним словом ПРИНЯТО.";
            int first = provider.Calls.Count;
            try
            {
                var response = await provider.SendMessageAsync(new() { Model = settings.Model!, Messages = [.. history, new() { Role = "user", Content = overflow }] }, context.CancellationToken);
                await RecordAsync("overflow", "См. полный запрос api-requests.jsonl; вход длиннее заявленного окна", "Фактическая ошибка превышения окна модели", response.Content,
                    false, first, history, failure: "Модель приняла запрос: превышение фактического лимита не подтверждено. Проверьте заявленное окно.");
            }
            catch (HttpRequestException e) when (TokenUsageCalculator.IsContextLimitError(e.Message))
            {
                await RecordAsync("overflow", "Вход длиннее заявленного окна; полный запрос сохранён", "Ошибка превышения контекста", e.Message, true, first, history, failure: e.Message);
            }
        }

        ToolCallingAgent Agent(ConversationService service) => new(service, new FixedResolver(provider), new AgentToolRegistry(), new(service));
        async Task<int> CreateConversationAsync(ConversationService service)
        {
            int id = await service.CreateConversationAsync(context.CancellationToken);
            var conversation = (await service.GetConversationAsync(id))!;
            conversation.Model = settings.Model!; conversation.CompressionEnabled = false;
            conversation.SystemPrompt = "Ты полезный ассистент. Следуй запросу. Если требуется инструмент — используй его; результаты не выдумывай. Для зависимых вычислений сначала получи результат первого инструмента.";
            return id;
        }
        async Task<AppDbContext> OpenDatabaseAsync(string name)
        {
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=" + Path.Combine(context.Directory, name)).Options);
            await db.Database.EnsureCreatedAsync(context.CancellationToken); return db;
        }
        async Task<string> RequestAsync(string group, string input, string expected, List<ChatMessage> history,
            Func<string, ChatRequest> build, Func<string, bool> assess, bool captureFormat = false, bool tolerateError = false)
        {
            int first = provider.Calls.Count;
            context.Output.WriteLine($"ЭТАП {group}\nВОПРОС: {input}\nОЖИДАНИЕ: {expected}; предыдущая история {history.Count} сообщений. Ожидание API…");
            try
            {
                var result = await provider.SendMessageAsync(build(input), context.CancellationToken);
                await RecordAsync(group, input, expected, result.Content, !string.IsNullOrWhiteSpace(result.Content) && assess(result.Content), first, history, captureFormat);
                return result.Content;
            }
            catch (Exception e) when (tolerateError && e is not OperationCanceledException)
            {
                await RecordAsync(group, input, expected, "", false, first, history, captureFormat, e.Message); return "";
            }
        }
        async Task RecordAsync(string group, string input, string expected, string answer, bool passed, int first,
            IReadOnlyList<ChatMessage> history, bool format = false, string? failure = null)
        {
            var calls = provider.Calls.Skip(first).ToList();
            var validated = ValidateFormat(answer);
            var row = new Observation(observations.Count + 1, group, input, expected, answer, passed,
                Enumerable.Range(first + 1, calls.Count).ToList(), calls.FirstOrDefault()?.EstimatedTokens ?? 0,
                TokenUsageCalculator.EstimateMessages(history), calls.All(c => c.Response is { PromptTokens: > 0 }) ? calls.Sum(c => c.Response!.PromptTokens) : null,
                calls.All(c => c.Response is { CompletionTokens: > 0 }) ? calls.Sum(c => c.Response!.CompletionTokens) : null,
                format ? validated.Parseable : null, format ? validated.Schema : null, failure)
            {
                EstimatedResponseTokens = TokenUsageCalculator.EstimateText(answer),
                CostRubles = calls.All(c => c.Response is { PromptTokens: > 0, CompletionTokens: > 0 })
                    ? PricingCatalog.Default.Estimate(settings.Model, calls.Sum(c => c.Response!.PromptTokens), calls.Sum(c => c.Response!.CompletionTokens)) : null,
                CumulativeCostRubles = provider.Calls.All(c => c.Response is { PromptTokens: > 0, CompletionTokens: > 0 })
                    ? PricingCatalog.Default.Estimate(settings.Model, provider.Calls.Sum(c => c.Response!.PromptTokens), provider.Calls.Sum(c => c.Response!.CompletionTokens)) : null
            };
            observations.Add(row);
            var stage = new ExperimentStage { Id = "step-" + row.Number, Title = $"{row.Number}. {group}", Description = input, CallNumbers = row.CallNumbers };
            stage.Metrics.Add(new("Ожидание", expected)); stage.Metrics.Add(new("Ответ", answer));
            stage.Metrics.Add(new("Проверка", passed ? "Пройдена" : "Расхождение"));
            stage.Metrics.Add(new("Оценка входа / истории", $"{row.EstimatedRequestTokens} / {row.HistoryTokens}"));
            stage.Metrics.Add(new("Оценка ответа / стоимость хода / накопленная стоимость", $"{row.EstimatedResponseTokens} / {row.CostRubles?.ToString() ?? "Н/д"} ₽ / {row.CumulativeCostRubles?.ToString() ?? "Н/д"} ₽"));
            if (format) stage.Metrics.Add(new("Парсинг / схема", $"{row.Parseable} / {row.SchemaValid}"));
            if (failure != null) stage.Metrics.Add(new("Ошибка", failure));
            presentation.Stages.Add(stage);
            context.Output.WriteLine($"\nЭТАП {stage.Title}\nВОПРОС: {input}\nОЖИДАНИЕ: {expected}\nОТВЕТ: {answer}\nПРОВЕРКА: {passed}; парсинг={row.Parseable}; схема={row.SchemaValid}; оценка входа={row.EstimatedRequestTokens}, истории={row.HistoryTokens}; usage API={row.PromptTokens}/{row.CompletionTokens}; ошибка={failure ?? "нет"}");
            await SaveAsync();
        }
        async Task SaveAsync()
        {
            await WriteFile("report.json", JsonSerializer.Serialize(new Report(day, completed, error, observations, evidence, conclusion), FileRunStore.Json));
            await WriteFile("experiment-view.json", JsonSerializer.Serialize(presentation, FileRunStore.Json));
            var md = new StringBuilder($"# День {day}: {CourseAssignments.Find("day" + day)!.Title}\n\nПротокол завершён: {completed}. Версии {ScenarioVersion}/{ScorerVersion}. Ошибка: {error ?? "нет"}.\n\nПроверки: {observations.Count(o => o.Passed)}/{observations.Count}.\n\n{conclusion}\n");
            foreach (var o in observations) md.AppendLine($"\n## {o.Number}. {o.Group}\n\n{o.Input}\n\nОжидание: {o.Expected}\n\nОтвет: {o.Answer}\n\nПройдено: {o.Passed}. Локальная оценка входа/истории/ответа: {o.EstimatedRequestTokens}/{o.HistoryTokens}/{o.EstimatedResponseTokens}. API input/output: {o.PromptTokens}/{o.CompletionTokens}. Стоимость хода/накопленная: {o.CostRubles?.ToString() ?? "Н/д"}/{o.CumulativeCostRubles?.ToString() ?? "Н/д"} ₽ по снимку тарифов (не списание). JSON/schema: {o.Parseable}/{o.SchemaValid}. Ошибка: {o.Error ?? "нет"}.");
            await WriteFile("report.md", md.ToString());
        }
        Task WriteFile(string name, string text) => SnapshotFile.WriteAllTextAsync(Path.Combine(context.Directory, name), text, CancellationToken.None);
    }

    public static (bool Parseable, bool Schema) ValidateFormat(string answer)
    {
        try
        {
            using var doc = JsonDocument.Parse(answer);
            var root = doc.RootElement;
            bool valid = root.ValueKind == JsonValueKind.Object && root.EnumerateObject().Count() == 1 && root.TryGetProperty("colors", out var colors) &&
                colors.ValueKind == JsonValueKind.Array && colors.GetArrayLength() == 3 && colors.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Object &&
                    item.EnumerateObject().Count() == 2 && item.TryGetProperty("color", out var c) && c.ValueKind == JsonValueKind.String &&
                    item.TryGetProperty("meaning", out var m) && m.ValueKind == JsonValueKind.String);
            return (true, valid);
        }
        catch (JsonException) { return (false, false); }
    }
    public static bool VerifyDependentTools(IReadOnlyList<ChatMessage> history, string answer)
    {
        try
        {
            var dice = history.FirstOrDefault(m => m.Role == "tool" && m.FunctionName == "roll_dice");
            var calc = history.LastOrDefault(m => m.Role == "tool" && m.FunctionName == "calculate");
            if (dice == null || calc == null) return false;
            int total = JsonDocument.Parse(dice.Content).RootElement.GetProperty("total").GetInt32();
            double result = JsonDocument.Parse(calc.Content).RootElement.GetProperty("result").GetDouble();
            return result == total * 7 && answer.Contains(((int)result).ToString()) && history.ToList().IndexOf(dice) < history.ToList().IndexOf(calc);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
    }
    public static Task<int> HelpAsync(string[] args)
    {
        Console.WriteLine("Дни 1/2/6/7/8: --provider ID --model MODEL --auto; day8: --context-window N (реальное окно модели). Проверки: dotnet run --project tests/UniversalAI.Checks.");
        return Task.FromResult(args.Any(a => a is "--help" or "-h" or "help") ? 0 : 2);
    }
    private sealed record Call(ChatRequest Request, ChatResult? Response, int EstimatedTokens);
    private sealed class ObservedProvider(IAIProvider inner) : IAIProvider
    {
        public string Name => inner.Name;
        public ProviderCapabilities Capabilities => inner.Capabilities;
        public List<Call> Calls { get; } = [];
        public async Task<ChatResult> SendMessageAsync(ChatRequest request, CancellationToken ct = default)
        {
            // Freeze messages: production tool loops append to the original list after each call.
            var snapshot = JsonSerializer.Deserialize<ChatRequest>(JsonSerializer.Serialize(request))!;
            int index = Calls.Count; Calls.Add(new(snapshot, null, TokenUsageCalculator.EstimateRequest(snapshot)));
            var result = await inner.SendMessageAsync(request, ct);
            Calls[index] = Calls[index] with { Response = result }; return result;
        }
        public IAsyncEnumerable<string> StreamMessageAsync(ChatRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class FixedResolver(IAIProvider provider) : IProviderResolver
    {
        public Task<IAIProvider> GetProviderForConversationAsync(int id, CancellationToken ct = default) => Task.FromResult(provider);
    }
}
