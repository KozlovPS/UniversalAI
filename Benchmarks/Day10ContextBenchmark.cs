using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UniversalAI.Core;
using UniversalAI.Core.Capabilities;
using UniversalAI.Providers;
using UniversalAI.Providers.OpenAICompatible;
using UniversalAI.Services.Agents;
using UniversalAI.Services.Context;

namespace UniversalAI.Benchmarks;

public static class Day10ContextBenchmark
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains("--help"))
        {
            Console.WriteLine("""
                Day-10: Sliding Window / Sticky Facts / summary Дня 9 и две ветки.
                dotnet run -- --benchmark day10 [--model openai/gpt-4o-mini]
                  [--transcript results/day9/transcript.json] [--generate]
                  [--keep 6] [--batch 10] [--output results/day10/<UTC>]
                  [--provider ProxyAPI] [--base-url URL] [--api-key-env NAME]
                  [--skip-branches] [--auto]
                По умолчанию используется сохранённый диалог Дня 9; все проверочные ответы
                и обновления памяти получаются НОВЫМИ платными запросами к API.
                --generate создаёт один новый общий диалог (ещё 15 API-вызовов).
                --skip-branches пропускает отдельный эксперимент ветвления.
                --auto запускает без начальной паузы. Без флага перед API нажмите Enter.
                --self-test — офлайн-проверки с подставной моделью, без ключа и сети.
                --rescore КАТАЛОГ — пересчитать сохранённые ответы и отчёт без API.
                """);
            return 0;
        }
        if (args.SequenceEqual(new[] { "--self-test" })) return await Day10ContextChecks.RunAsync();
        try
        {
            if (args.Length == 2 && args[0] == "--rescore")
            {
                var saved = JsonSerializer.Deserialize<Day10Report>(await File.ReadAllTextAsync(
                    Path.Combine(args[1], "report.json")), Json) ?? throw new InvalidOperationException("Нет отчёта.");
                foreach (var result in saved.Strategies)
                {
                    foreach (var answer in result.Answers)
                        answer.Correct = Day9CompressionScenario.IsCorrect(
                            Day9CompressionScenario.Probes.Single(p => p.Detail == answer.Detail), answer.Answer);
                    foreach (var answer in result.Branches)
                        answer.Correct = Day9CompressionScenario.IsCorrect(
                            new(answer.Detail, answer.Expected, answer.Question, answer.Accepted), answer.Answer);
                }
                await SaveReportAsync(saved, args[1]);
                return 0;
            }
            var options = OpenAICompatibleOptions.FromEnvironment();
            string model = "openai/gpt-4o-mini", transcriptPath = "results/day9/transcript.json";
            string output = Path.Combine("results", "day10", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
            int keep = 6, batch = 10;
            bool generate = false, branches = true, automatic = false;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--generate": generate = true; continue;
                    case "--skip-branches": branches = false; continue;
                    case "--auto": automatic = true; continue;
                }
                string option = args[i];
                if (++i == args.Length) throw new ArgumentException($"Нет значения для {option}.");
                switch (option)
                {
                    case "--model": model = args[i]; break;
                    case "--output": output = args[i]; break;
                    case "--transcript": transcriptPath = args[i]; break;
                    case "--keep": keep = int.Parse(args[i]); break;
                    case "--batch": batch = int.Parse(args[i]); break;
                    case "--provider": options.Name = args[i]; break;
                    case "--base-url": options.BaseUrl = args[i]; break;
                    case "--api-key-env": options.ApiKeyEnvironmentVariable = args[i]; break;
                    default: throw new ArgumentException($"Неизвестный параметр {option}.");
                }
            }
            if (keep < 2 || batch < 1) throw new ArgumentException("keep >= 2; batch >= 1.");
            if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
                throw new ArgumentException("Каталог результатов уже содержит файлы. Укажите новый --output, чтобы не смешивать прогоны.");
            List<ChatMessage>? transcript = generate ? null :
                JsonSerializer.Deserialize<List<ChatMessage>>(await File.ReadAllTextAsync(transcriptPath), Json);
            if (!generate) ValidateTranscript(transcript);
            using HttpClient http = new() { Timeout = TimeSpan.FromMinutes(3) };
            var api = new OpenAICompatibleProvider(http, options);
            api.ValidateApiKey();
            Console.WriteLine($"Day-10: {options.Name}, {model}; N={keep}, batch={batch}.");
            Console.WriteLine(branches
                ? "Полный прогон с ветками: при настройках по умолчанию 202 платных API-вызова."
                : "Без веток: при настройках по умолчанию 70 платных API-вызовов.");
            if (generate) Console.WriteLine("Создание общего диалога: дополнительно 15 вызовов.");
            Console.WriteLine($"Результаты: {Path.GetFullPath(output)}");
            if (!automatic && !Console.IsInputRedirected)
            {
                Console.Write("Нажмите Enter для начала... ");
                Console.ReadLine();
            }
            Directory.CreateDirectory(output);
            var recorder = new Day10RecordingProvider(api, output);
            await ExecuteAsync(recorder, output, model, keep, batch, transcript, branches);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Day-10: {exception.Message}");
            return 1;
        }
    }

    internal static void ValidateTranscript(List<ChatMessage>? transcript)
    {
        if (transcript == null || transcript.Count != 30 || transcript.Where((m, i) =>
                m.Role != (i % 2 == 0 ? "user" : "assistant") || string.IsNullOrWhiteSpace(m.Content) ||
                m.ToolCalls?.Count > 0 || (i % 2 == 0 && m.Content != Day9CompressionScenario.Messages[i / 2])).Any())
            throw new ArgumentException("Нужен диалог Дня 9: 15 исходных user-реплик и 15 непустых assistant-ответов.");
    }

    internal static async Task<Day10Report> ExecuteAsync(Day10RecordingProvider provider, string output,
        string model, int keep, int batch, List<ChatMessage>? transcript, bool branches)
    {
        Directory.CreateDirectory(output);
        await WriteAsync(output, "scenario.json", new
        {
            Day9CompressionScenario.SystemPrompt, Day9CompressionScenario.Messages,
            Day9CompressionScenario.Probes, Model = model, Keep = keep, Batch = batch,
            Branches = branches, Source = transcript == null ? "generated" : "replayed", provider.Name
        });
        if (transcript == null)
        {
            provider.Scope = "preparation";
            var generator = new Day10ContextSession(new() { Model = model, Keep = 1000 }, provider);
            foreach (string text in Day9CompressionScenario.Messages)
            {
                await generator.AskAsync(text);
                await WriteAsync(output, "transcript.json", generator.State.History);
            }
            transcript = generator.State.History;
        }
        ValidateTranscript(transcript);
        await WriteAsync(output, "transcript.json", transcript);
        Day10Report report = new() { Model = model, Provider = provider.Name, Keep = keep, Batch = batch };
        foreach (ContextStrategy strategy in Enum.GetValues<ContextStrategy>())
        {
            Console.WriteLine($"\n=== {strategy}: воспроизведение и 13 вопросов ===");
            provider.Strategy = strategy.ToString(); provider.Branch = "base"; provider.Scope = "comparison";
            var session = new Day10ContextSession(new() { Strategy = strategy, Keep = keep, Batch = batch, Model = model }, provider);
            for (int i = 0; i < transcript.Count; i += 2)
                await session.ReplayAsync(transcript[i], transcript[i + 1]);
            var context = await session.PrepareAsync(force: true);
            string checkpoint = session.Checkpoint();
            await WriteAsync(output, $"{strategy}-checkpoint.json", session.State);
            Day10StrategyResult result = new()
            {
                Strategy = strategy, ContextMessages = context.Count,
                ContextTokenEstimate = TokenUsageCalculator.EstimateMessages(context),
                UsesSummary = strategy == ContextStrategy.Summary && context.Any(m => m.Role == "system"),
                SourceFiles = SourceFiles(strategy)
            };
            foreach (MemoryProbe probe in Day9CompressionScenario.Probes)
            {
                // A fresh restored state prevents answers and facts updates leaking between probes.
                var independent = Day10ContextSession.Restore(checkpoint, provider);
                var response = await independent.AskAsync(probe.Question);
                result.Answers.Add(new()
                {
                    Detail = probe.Detail, Question = probe.Question, Expected = probe.Expected,
                    Answer = response.Content, Correct = Day9CompressionScenario.IsCorrect(probe, response.Content)
                });
                await WriteAsync(output, $"{strategy}-answers.json", result.Answers);
            }
            if (branches) await RunBranchesAsync(session, checkpoint, provider, result, output);
            result.ProductionLines = CountSourceLines(result.SourceFiles);
            report.Strategies.Add(result);
            report.Calls = provider.Calls.ToList();
            await SaveReportAsync(report, output);
        }
        report.Complete = true;
        report.Calls = provider.Calls.ToList();
        await SaveReportAsync(report, output);
        Console.WriteLine(await File.ReadAllTextAsync(Path.Combine(output, "report.md")));
        return report;
    }

    private static async Task RunBranchesAsync(Day10ContextSession original, string checkpoint,
        Day10RecordingProvider provider, Day10StrategyResult result, string output)
    {
        provider.Scope = "branches";
        var a = Day10ContextSession.Restore(checkpoint, provider);
        var b = Day10ContextSession.Restore(checkpoint, provider);
        var sessions = new[] { (Name: "A", Session: a, Budget: "390000", Day: "21", Time: "08:45", Code: "ЛИПА-47"),
                               (Name: "B", Session: b, Budget: "460000", Day: "28", Time: "10:15", Code: "ИВА-82") };
        foreach (var branch in sessions)
        {
            provider.Branch = branch.Name;
            await branch.Session.AskAsync($"Меняем утверждённые договорённости: бюджет теперь {branch.Budget} рублей, " +
                $"срок сдачи — {branch.Day} декабря 2026 года, ежедневное напоминание — в {branch.Time}. " +
                $"Код согласования этой ветки — {branch.Code}. Остальные решения сохраняются. Подтверди одним предложением.");
            await ProbeBranchAsync(branch.Name, "immediate", branch.Session.Checkpoint(), branch.Budget,
                branch.Day, branch.Time, branch.Code, provider, result);
        }
        // Switch A -> B -> A through actual independent session objects.
        foreach (var branch in sessions)
        {
            provider.Branch = branch.Name;
            foreach (string text in Day9CompressionScenario.Messages.Skip(8)) await branch.Session.AskAsync(text);
            await branch.Session.PrepareAsync(force: true);
        }
        foreach (var branch in sessions)
        {
            provider.Branch = branch.Name;
            string saved = branch.Session.Checkpoint();
            // Round-trip the complete checkpoint through disk, not just object references.
            string path = Path.Combine(output, $"{result.Strategy}-branch-{branch.Name}.json");
            await File.WriteAllTextAsync(path, saved);
            var reopened = Day10ContextSession.Restore(await File.ReadAllTextAsync(path), provider);
            await ProbeBranchAsync(branch.Name, "delayed", reopened.Checkpoint(), branch.Budget,
                branch.Day, branch.Time, branch.Code, provider, result);
            if (reopened.Checkpoint() != saved) throw new InvalidOperationException("Восстановление изменило ветку.");
        }
        provider.Branch = "A";
        if (a.State.History.Count != b.State.History.Count || a.State.History.Any(m => m.Content.Contains("ИВА-82")) ||
            b.State.History.Any(m => m.Content.Contains("ЛИПА-47")) ||
            a.State.Facts.Values.Any(v => v.Contains("ИВА-82")) ||
            b.State.Facts.Values.Any(v => v.Contains("ЛИПА-47")) ||
            (a.State.Summary?.Content.Contains("ИВА-82") ?? false) ||
            (b.State.Summary?.Content.Contains("ЛИПА-47") ?? false) || original.Checkpoint() != checkpoint)
            throw new InvalidOperationException("Утечка данных между ветками или изменение checkpoint.");
        result.BranchIsolationPassed = true;
    }

    private static async Task ProbeBranchAsync(string branch, string stage, string checkpoint,
        string budget, string day, string time, string code, Day10RecordingProvider provider, Day10StrategyResult result)
    {
        MemoryProbe[] probes =
        [
            new("Бюджет", budget, "Какой актуальный бюджет в рублях? Верни только число.", [budget]),
            new("Срок", $"{day} декабря 2026", "Какой точный срок сдачи, включая год?", [$"{day}декабря2026", $"{day}122026", $"202612{day}"]),
            new("Напоминание", time, "В какое время появляется ежедневное напоминание?", [time]),
            new("Код", code, "Какой код согласования этой ветки?", [code])
        ];
        foreach (var probe in probes)
        {
            var response = await Day10ContextSession.Restore(checkpoint, provider).AskAsync(probe.Question);
            result.Branches.Add(new()
            {
                Branch = branch, Stage = stage, Detail = probe.Detail, Question = probe.Question,
                Expected = probe.Expected, Accepted = probe.Accepted, Answer = response.Content,
                Correct = Day9CompressionScenario.IsCorrect(probe, response.Content)
            });
        }
    }

    private static string[] SourceFiles(ContextStrategy strategy) => strategy switch
    {
        ContextStrategy.SlidingWindow => ["Services/Context/SlidingWindowMemory.cs"],
        ContextStrategy.StickyFacts => ["Services/Context/StickyFactsMemory.cs", "Services/Context/SlidingWindowMemory.cs"],
        _ => ["Services/ContextCompressionService.cs", "Data/Entities/ContextSummaryEntity.cs"]
    };

    private static int? CountSourceLines(string[] files)
    {
        if (files.Any(f => !File.Exists(f))) return null;
        return files.Sum(f => File.ReadLines(f).Count(line => !string.IsNullOrWhiteSpace(line)));
    }

    internal static Task WriteAsync(string output, string name, object value) =>
        File.WriteAllTextAsync(Path.Combine(output, name), JsonSerializer.Serialize(value, Json), new UTF8Encoding(false));

    internal static async Task SaveReportAsync(Day10Report report, string output)
    {
        await WriteAsync(output, "report.json", report);
        StringBuilder md = new();
        md.AppendLine($"# Day-10: управление контекстом\n\nUTC: {report.CreatedAt:O}; {report.Provider}, `{report.Model}`; N={report.Keep}, batch={report.Batch}.");
        md.AppendLine($"\nПрогон завершён: {report.Complete}. Исходный диалог: 30 одинаковых сообщений, 13 независимых вопросов.");
        md.AppendLine("\n| Стратегия | Деталей | Вопросы: вход / выход | Память: вход / выход | Всего токенов | Вызовы памяти / вопросов | Непустых строк / файлов |\n|---|---:|---:|---:|---:|---:|---:|");
        foreach (var result in report.Strategies)
        {
            var calls = report.Calls.Where(c => c.Strategy == result.Strategy.ToString() && c.Scope == "comparison").ToArray();
            var memory = calls.Where(c => c.Kind == "memory").ToArray();
            var probes = calls.Where(c => c.Kind != "memory").ToArray();
            md.AppendLine($"| {result.Strategy} | {result.Answers.Count(a => a.Correct)}/13 | {Usage(probes, true)} / {Usage(probes, false)} | " +
                $"{Usage(memory, true)} / {Usage(memory, false)} | {Total(calls)} | {memory.Length} / {probes.Length} | {result.ProductionLines?.ToString() ?? "н/д"} / {result.SourceFiles.Length} |");
        }
        md.AppendLine("\nТокены — usage API, включая все обновления памяти и обработку контрольных вопросов. Общий диалог воспроизводится; его прежняя генерация не включена. " +
            $"Новая генерация, если запрошена: {report.Calls.Count(c => c.Scope == "preparation")} вызовов, {Total(report.Calls.Where(c => c.Scope == "preparation"))} токенов.");
        md.AppendLine("\n| Деталь | Эталон | Sliding Window | Sticky Facts | Summary |\n|---|---|---|---|---|");
        foreach (var probe in Day9CompressionScenario.Probes)
        {
            var cells = Enum.GetValues<ContextStrategy>().Select(strategy =>
            {
                var a = report.Strategies.SingleOrDefault(s => s.Strategy == strategy)?.Answers.SingleOrDefault(p => p.Detail == probe.Detail);
                return a == null ? "—" : $"{(a.Correct ? "✓" : "✗")} {Cell(a.Answer)}";
            });
            md.AppendLine($"| {Cell(probe.Detail)} | {Cell(probe.Expected)} | {string.Join(" | ", cells)} |");
        }
        md.AppendLine("\n## Ветки\n\n| Стратегия | A сразу / позже | B сразу / позже | Изоляция состояния | Токены / вызовы веток |\n|---|---|---|---|---|");
        foreach (var result in report.Strategies)
        {
            string Score(string branch, string stage) => result.Branches.Any(a => a.Branch == branch && a.Stage == stage)
                ? $"{result.Branches.Count(a => a.Branch == branch && a.Stage == stage && a.Correct)}/4" : "—";
            var calls = report.Calls.Where(c => c.Strategy == result.Strategy.ToString() && c.Scope == "branches").ToArray();
            md.AppendLine($"| {result.Strategy} | {Score("A", "immediate")} / {Score("A", "delayed")} | {Score("B", "immediate")} / {Score("B", "delayed")} | " +
                $"{(result.BranchIsolationPassed ? "пройдена" : "не проверялась")} | {Total(calls)} / {calls.Length} |");
        }
        md.AppendLine("\nПосле изменений в каждой ветке проигрываются 7 отвлекающих ходов. Ответы на проверочные вопросы не изменяют ветку. " +
            "Checkpoint и ветки сохраняются в JSON и восстанавливаются в независимые объекты; проверяются история, facts, summary и неизменность исходного состояния.");
        md.AppendLine("\n## Сложность и вывод\n");
        foreach (var result in report.Strategies)
        {
            md.AppendLine($"- {result.Strategy}: исходники {string.Join(", ", result.SourceFiles.Select(f => $"`{f}`"))}; " +
                $"контекст ~{result.ContextTokenEstimate} токенов / {result.ContextMessages} сообщений. " +
                (result.Strategy == ContextStrategy.Summary ? $"Summary фактически использовано: {result.UsesSummary}." : ""));
            var lost = result.Answers.Where(a => !a.Correct).Select(a => a.Detail).ToArray();
            md.AppendLine($"  Не сохранены: {(lost.Length == 0 ? "не обнаружено" : string.Join(", ", lost))}.");
        }
        md.AppendLine("\nСтроки считаются вместе с промптами и комментариями, без пустых строк. Это размер перечисленных модулей, а не полный объём разработки. " +
            "Общие зависимости хранения summary (`ConversationService`, EF/SQLite) не распределяются по стратегиям; общий агент бенчмарка — `Benchmarks/Day10ContextSession.cs`, " +
            $"{CountSourceLines(["Benchmarks/Day10ContextSession.cs"])?.ToString() ?? "н/д"} непустых строк. Все три стратегии здесь переключаются в агенте бенчмарка; обычный консольный чат этим запуском не изменяется.");
        md.AppendLine("\nSliding Window подходит коротким задачам, где важны последние реплики и допустима потеря ранних договорённостей. " +
            "Sticky Facts подходит точным ограничениям, предпочтениям, бюджетам и срокам; обновление после каждой реплики добавляет вызовы модели. " +
            "Summary подходит длинным обсуждениям с решениями и открытыми вопросами; обновляется пакетами, но может терять точные детали. " +
            "Частые обновления facts могут стоить дороже summary. Низкий результат Sliding Window не нарушает контракт окна.");
        md.AppendLine("\nОдин прогон, лексическая оценка; ответы сохранены для ручной проверки противоречий. Локальная оценка контекста не является usage API. " +
            "Summary использует исходную реализацию Дня 9: хвост может быть длиннее N, а слишком длинное summary заменяется полной историей.");
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), md.ToString(), new UTF8Encoding(false));
        var rows = new List<string> { "Strategy,Branch,Stage,Detail,Expected,Answer,Correct" };
        foreach (var result in report.Strategies)
            foreach (var answer in result.Answers.Concat(result.Branches))
                rows.Add(string.Join(",", new[] { result.Strategy.ToString(), answer.Branch, answer.Stage,
                    answer.Detail, answer.Expected, answer.Answer, answer.Correct.ToString() }.Select(Csv)));
        await File.WriteAllLinesAsync(Path.Combine(output, "answers.csv"), rows, new UTF8Encoding(true));
    }

    private static string Cell(string value) => value.Replace("|", "\\|").Replace("\r", "").Replace("\n", "<br>");
    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    private static string Usage(IEnumerable<Day10Call> calls, bool input) => calls.Any(c => !c.UsageAvailable)
        ? "н/д" : calls.Sum(c => input ? c.Response!.PromptTokens : c.Response!.CompletionTokens).ToString();
    private static string Total(IEnumerable<Day10Call> calls) => calls.Any(c => !c.UsageAvailable) ? "н/д" :
        calls.Sum(c => c.Response!.TotalTokens > 0 ? c.Response.TotalTokens : c.Response.PromptTokens + c.Response.CompletionTokens).ToString();
}

public sealed class Day10Report
{
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public int Keep { get; set; }
    public int Batch { get; set; }
    public bool Complete { get; set; }
    public List<Day10StrategyResult> Strategies { get; set; } = [];
    public List<Day10Call> Calls { get; set; } = [];
}

public sealed class Day10StrategyResult
{
    public ContextStrategy Strategy { get; set; }
    public List<Day10Answer> Answers { get; set; } = [];
    public List<Day10Answer> Branches { get; set; } = [];
    public bool BranchIsolationPassed { get; set; }
    public bool UsesSummary { get; set; }
    public int ContextMessages { get; set; }
    public int ContextTokenEstimate { get; set; }
    public string[] SourceFiles { get; set; } = [];
    public int? ProductionLines { get; set; }
}

public sealed class Day10Answer
{
    public string Branch { get; set; } = "base";
    public string Stage { get; set; } = "final";
    public string Detail { get; set; } = "";
    public string Question { get; set; } = "";
    public string Expected { get; set; } = "";
    public string[] Accepted { get; set; } = [];
    public string Answer { get; set; } = "";
    public bool Correct { get; set; }
}

public sealed class Day10Call
{
    public int Number { get; set; }
    public string Strategy { get; set; } = "";
    public string Branch { get; set; } = "";
    public string Scope { get; set; } = "";
    public string Kind { get; set; } = "";
    public ChatRequest Request { get; set; } = new();
    public ChatResult? Response { get; set; }
    public string? Error { get; set; }
    public double Seconds { get; set; }
    public bool UsageAvailable => Response != null && (Response.PromptTokens > 0 || Response.CompletionTokens > 0);
}

public sealed class Day10RecordingProvider(IAIProvider inner, string output) : IAIProvider
{
    public string Name => inner.Name;
    public ProviderCapabilities Capabilities => inner.Capabilities;
    public string Strategy { get; set; } = "shared";
    public string Branch { get; set; } = "base";
    public string Scope { get; set; } = "comparison";
    public List<Day10Call> Calls { get; } = [];

    public async Task<ChatResult> SendMessageAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        var call = new Day10Call
        {
            Number = Calls.Count + 1, Strategy = Strategy, Branch = Branch, Scope = Scope,
            Kind = request.Messages[0].Content.StartsWith("Сожми историю") || request.Messages[0].Content == StickyFactsMemory.Instructions
                ? "memory" : "answer",
            Request = JsonSerializer.Deserialize<ChatRequest>(JsonSerializer.Serialize(request))!
        };
        Console.WriteLine($"API #{call.Number}: {call.Strategy}/{Branch}/{Scope}/{call.Kind}");
        Stopwatch clock = Stopwatch.StartNew();
        // Record the request before network I/O so interrupted runs remain inspectable.
        await File.AppendAllTextAsync(Path.Combine(output, "requests.jsonl"), JsonSerializer.Serialize(call) + "\n", cancellationToken);
        try
        {
            call.Response = await inner.SendMessageAsync(request, cancellationToken);
            return call.Response;
        }
        catch (Exception exception)
        {
            call.Error = exception.Message;
            throw;
        }
        finally
        {
            call.Seconds = clock.Elapsed.TotalSeconds;
            Calls.Add(call);
            await File.AppendAllTextAsync(Path.Combine(output, "calls.jsonl"), JsonSerializer.Serialize(call) + "\n");
            if (call.Response != null)
                Console.WriteLine($"  {call.Response.Content}\n  usage: {call.Response.PromptTokens}/{call.Response.CompletionTokens}; {call.Seconds:F2} с");
        }
    }

    public IAsyncEnumerable<string> StreamMessageAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
