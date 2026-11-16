using UniversalAI.Benchmarks.Runtime;
using UniversalAI.Providers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UniversalAI.Core;
using UniversalAI.Data;
using UniversalAI.Data.Entities;
using UniversalAI.Providers.OpenAICompatible;
using UniversalAI.Services;
using UniversalAI.Services.Agents;

namespace UniversalAI.Benchmarks;

public static class Day9CompressionBenchmark
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains("--help"))
        {
            BenchmarkOutput.WriteLine("""
                Day-9: 30 сообщений, затем независимые вопросы по заранее заданным деталям.
                dotnet run -- --benchmark day9 [--model openai/gpt-4o-mini]
                  [--output results/day9] [--provider ProxyAPI] [--base-url URL] [--api-key-env NAME]
                Реальные API-запросы, все реплики, summary, статистика и вывод по этапам.
                Между этапами нажимайте Enter.
                --auto       Пройти этапы автоматически, без пауз.
                --self-test  Проверки с SQLite и подставным провайдером, без сети и API-ключа.
                --rescore КАТАЛОГ  Пересчитать оценку сохранённых ответов без API-вызовов.
                По умолчанию: keep=6, batch=10. Расход summary включён в сравнение.
                """);
            return 0;
        }

        if (args.SequenceEqual(new[] { "--self-test" })) return await Day9CompressionChecks.RunAsync();
        if (args.Length == 2 && args[0] == "--rescore")
        {
            Report saved = JsonSerializer.Deserialize<Report>(await File.ReadAllTextAsync(Path.Combine(args[1], "report.json")), Json)
                ?? throw new InvalidOperationException("Отчёт не найден.");
            var rescored = saved.Probes.Select(p =>
            {
                MemoryProbe reference = Day9CompressionScenario.Probes.Single(probe => probe.Detail == p.Detail);
                return p with
                {
                    FullCorrect = Day9CompressionScenario.IsCorrect(reference, p.Full.Content),
                    CompressedCorrect = Day9CompressionScenario.IsCorrect(reference, p.Compressed.Content)
                };
            }).ToList();
            await SaveReportAsync(saved with { Probes = rescored }, args[1]);
            return 0;
        }

        try
        {
            OpenAICompatibleOptions options = OpenAICompatibleOptions.FromEnvironment();
            string model = "openai/gpt-4o-mini";
            string output = "results/day9";
            bool automatic = false;
            for (int i = 0; i < args.Length; i++)
            {
                string option = args[i];
                if (option == "--auto") { automatic = true; continue; }
                if (++i >= args.Length) throw new ArgumentException($"Нет значения для {option}.");
                switch (option)
                {
                    case "--model": model = args[i]; break;
                    case "--output": output = args[i]; break;
                    case "--provider": options.Name = args[i]; break;
                    case "--base-url": options.BaseUrl = args[i]; break;
                    case "--api-key-env": options.ApiKeyEnvironmentVariable = args[i]; break;
                    default: throw new ArgumentException($"Неизвестный параметр: {option}");
                }
            }

            return await ExecuteCoreAsync(options, model, output, automatic);
        }
        catch (Exception e) when (e is not OperationCanceledException) { BenchmarkOutput.Error.WriteLine(e.Message); return 1; }
    }

    public static Task<int> ExecuteAsync(BenchmarkSettings settings, BenchmarkExecutionContext context) =>
        ExecuteCoreAsync(BenchmarkOutput.Options(context), settings.Model!, context.Directory, !context.Interactive);

    private static async Task<int> ExecuteCoreAsync(OpenAICompatibleOptions options, string model, string output, bool automatic)
    {
        try
        {
            Directory.CreateDirectory(output);
            // Fix the facts and questions on disk before any model answer exists.
            await WriteJsonAsync(Path.Combine(output, "scenario.json"), new
            {
                Day9CompressionScenario.Messages, Day9CompressionScenario.Probes
            });
            using HttpClient http = new() { Timeout = TimeSpan.FromMinutes(3) };
            IAIProvider apiProvider = BenchmarkOutput.Provider(options, http);
            Day9BenchmarkConsole provider = new(apiProvider, automatic);
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync();
            await using AppDbContext db = new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            ConversationService conversations = new(db);
            ContextCompressionService compression = new(conversations);
            int id = await conversations.CreateConversationAsync();
            ConversationEntity conversation = (await conversations.GetConversationAsync(id))!;
            conversation.Model = model;
            conversation.SystemPrompt = Day9CompressionScenario.SystemPrompt;
            await db.SaveChangesAsync();

            provider.Stage(1, "ЗАРАНЕЕ ФИКСИРУЕМ ДЕТАЛИ И УСЛОВИЯ ЭКСПЕРИМЕНТА");
            BenchmarkOutput.WriteLine($"Провайдер: {provider.Name}; endpoint: {options.BaseUrl}; модель: {model}.");
            BenchmarkOutput.WriteLine("Все ответы ниже будут получены новыми запросами к API. Готовые ответы не загружаются.");
            BenchmarkOutput.WriteLine($"Keep={conversation.ContextKeepLastMessages}; batch={conversation.ContextCompressionBatchSize}.");
            BenchmarkOutput.WriteLine("Сохраняем последние целые ходы; старые сообщения заменяем отдельным summary.");
            BenchmarkOutput.WriteLine("Детали, которые будем проверять (модель summary не получает этот список):");
            foreach (MemoryProbe probe in Day9CompressionScenario.Probes)
                BenchmarkOutput.WriteLine($"  {probe.Detail}: {probe.Expected}");
            BenchmarkOutput.WriteLine("В разговоре бюджет изменится с 480000 до 420000 рублей: проверяем актуальное значение.");
            BenchmarkOutput.WriteLine($"Эталон и сценарий уже записаны в {Path.GetFullPath(Path.Combine(output, "scenario.json"))}");
            BenchmarkOutput.WriteLine("Ожидается 44 платных API-вызова: 15 ответов диалога, 3 summary и 26 ответов на вопросы.");

            List<ChatMessage> transcript = [];
            List<ChatResult> dialogueCalls = [];
            provider.Stage(2, "ПРОВОДИМ ДИАЛОГ НА 30 СООБЩЕНИЙ");
            BenchmarkOutput.WriteLine($"Day-9: {provider.Name}, {model}. Создаём общий диалог из 30 сообщений.");
            foreach (string userText in Day9CompressionScenario.Messages)
            {
                ChatMessage user = new() { Role = "user", Content = userText };
                BenchmarkOutput.WriteLine($"\nПОЛЬЗОВАТЕЛЬ — сообщение {transcript.Count + 1}/30:\n{userText}");
                ChatResult reply = await provider.SendMessageAsync(Request(model, [.. transcript, user], 160));
                EnsureComplete(reply);
                dialogueCalls.Add(reply);
                transcript.Add(user);
                transcript.Add(new() { Role = "assistant", Content = reply.Content });
                await WriteJsonAsync(Path.Combine(output, "transcript.json"), transcript);
                BenchmarkOutput.WriteLine($"Диалог: {transcript.Count}/30 сообщений.");
            }

            // Replay exactly the same log at the same turn boundaries. Only compression is different.
            provider.Stage(3, "СЖИМАЕМ СТАРЫЕ СООБЩЕНИЯ, ХРАНИМ SUMMARY ОТДЕЛЬНО");
            provider.CallLabel = "СОЗДАНИЕ / ОБНОВЛЕНИЕ SUMMARY";
            BenchmarkOutput.WriteLine("Проигрываем один и тот же диалог по границам ходов. Это изолирует эффект сжатия.");
            BenchmarkOutput.WriteLine("При каждом обновлении модель получает прежнее summary и новую старую часть истории.");
            List<ChatResult> summaryCalls = [];
            for (int i = 0; i < transcript.Count; i += 2)
            {
                await conversations.SaveMessagesAsync(id, transcript.Skip(i).Take(2).ToList());
                PreparedContext replay = await compression.PrepareAsync(conversation, transcript.Take(i + 2).ToList(), provider);
                if (replay.SummaryCall != null)
                {
                    summaryCalls.Add(replay.SummaryCall);
                    BenchmarkOutput.WriteLine($"Сжатие: {replay.CoveredMessages} сообщений заменены памятью.");
                    BenchmarkOutput.WriteLine($"В контексте осталось {replay.RecentMessages} исходных сообщений; " +
                                                $"размер истории ~{replay.RawHistoryTokens} → ~{replay.EffectiveHistoryTokens} токенов.");
                }
            }

            PreparedContext compressed = await compression.PrepareAsync(conversation, transcript, provider, force: true);
            if (compressed.SummaryCall != null) summaryCalls.Add(compressed.SummaryCall);
            var summary = await conversations.GetSummaryAsync(id);
            await WriteJsonAsync(Path.Combine(output, "summary.json"), summary);
            BenchmarkOutput.WriteLine($"\nSummary сохранено отдельно: SQLite, таблица ContextSummaries; копия — {Path.Combine(output, "summary.json")}.");
            BenchmarkOutput.WriteLine($"В запросах режима сжатия: summary вместо {compressed.CoveredMessages} старых сообщений " +
                              $"+ {compressed.RecentMessages} исходных сообщений.");
            BenchmarkOutput.WriteLine($"История: ~{compressed.RawHistoryTokens} → ~{compressed.EffectiveHistoryTokens} токенов (локальная оценка).");
            BenchmarkOutput.WriteLine($"Summary реально используется: {(compressed.UsesSummary ? "ДА" : "НЕТ: исходный контекст оказался короче")}.");
            BenchmarkOutput.WriteLine("Последние сообщения, оставленные КАК ЕСТЬ:");
            foreach (ChatMessage message in transcript.TakeLast(compressed.RecentMessages))
                BenchmarkOutput.WriteLine($"\n[{message.Role}] {message.Content}");

            provider.Stage(4, "ЗАДАЁМ ВОПРОСЫ ПО КАЖДОЙ ДЕТАЛИ В ОБОИХ РЕЖИМАХ");
            BenchmarkOutput.WriteLine("Вопросы независимы. Ответ на один вопрос не попадает в контекст следующего.");
            List<ProbeResult> probes = [];
            foreach (MemoryProbe probe in Day9CompressionScenario.Probes)
            {
                // Probe answers are never appended: later questions cannot leak an earlier answer.
                ChatMessage question = new() { Role = "user", Content = probe.Question };
                BenchmarkOutput.WriteLine($"\nВОПРОС {probes.Count + 1}/{Day9CompressionScenario.Probes.Length}: {probe.Question}\nЭТАЛОН: {probe.Expected}");
                provider.CallLabel = "БЕЗ СЖАТИЯ: ПОЛНАЯ ИСТОРИЯ + ВОПРОС";
                ChatResult full = await provider.SendMessageAsync(Request(model, [.. transcript, question], 120));
                EnsureComplete(full);
                provider.CallLabel = "СО СЖАТИЕМ: SUMMARY + ПОСЛЕДНИЕ СООБЩЕНИЯ + ВОПРОС";
                ChatResult compact = await provider.SendMessageAsync(Request(model, [.. compressed.Messages, question], 120));
                EnsureComplete(compact);
                probes.Add(new(probe.Detail, probe.Expected, probe.Question, full, compact,
                    Day9CompressionScenario.IsCorrect(probe, full.Content),
                    Day9CompressionScenario.IsCorrect(probe, compact.Content)));
                await WriteJsonAsync(Path.Combine(output, "answers.json"), probes);
                BenchmarkOutput.WriteLine($"{probe.Detail}: без сжатия {(probes[^1].FullCorrect ? "OK" : "ошибка")}; " +
                                  $"со сжатием {(probes[^1].CompressedCorrect ? "OK" : "ошибка")}.");
            }

            Report report = new(DateTimeOffset.UtcNow, provider.Name, model, transcript.Count,
                conversation.ContextKeepLastMessages, conversation.ContextCompressionBatchSize,
                compressed.RawHistoryTokens, compressed.EffectiveHistoryTokens, compressed.UsesSummary,
                summary?.Content ?? "", probes, dialogueCalls, summaryCalls);
            await SaveReportAsync(report, output, provider);
            return 0;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            BenchmarkOutput.Error.WriteLine($"Day-9: {exception.Message}");
            return 1;
        }
    }

    private static async Task SaveReportAsync(Report report, string output, Day9BenchmarkConsole? console = null)
    {
        await WriteJsonAsync(Path.Combine(output, "report.json"), report);
        await WriteJsonAsync(Path.Combine(output, "answers.json"), report.Probes);
        string markdown = BuildMarkdown(report);
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), markdown, new UTF8Encoding(false));
        string csv = "Detail,Expected,FullAnswer,CompressedAnswer,FullCorrect,CompressedCorrect\n" +
                     string.Join("\n", report.Probes.Select(p => string.Join(",", new[]
                     {
                         p.Detail, p.Expected, p.Full.Content, p.Compressed.Content,
                         p.FullCorrect.ToString(), p.CompressedCorrect.ToString()
                     }.Select(v => "\"" + v.Replace("\"", "\"\"") + "\""))));
        await File.WriteAllTextAsync(Path.Combine(output, "answers.csv"), csv, new UTF8Encoding(true));
        if (console != null)
        {
            console.Stage(5, "СРАВНИВАЕМ ПАМЯТЬ И РАСХОД ТОКЕНОВ");
            BenchmarkOutput.WriteLine(BuildStatistics(report));
            console.Stage(6, "ВЫВОД ПО РЕАЛЬНОМУ ПРОГОНУ");
            BenchmarkOutput.WriteLine(BuildConclusion(report));
        }
        else BenchmarkOutput.WriteLine(markdown);
        BenchmarkOutput.WriteLine($"Результаты: {Path.GetFullPath(output)}");
    }

    private static ChatRequest Request(string model, List<ChatMessage> history, int maxTokens) => new()
    {
        Model = model, MaxTokens = maxTokens, Temperature = 0,
        Messages = [new() { Role = "system", Content = Day9CompressionScenario.SystemPrompt }, .. history]
    };

    private static void EnsureComplete(ChatResult result)
    {
        if (result.FinishReason is not ("stop" or "end_turn") || string.IsNullOrWhiteSpace(result.Content))
            throw new InvalidOperationException($"Незавершённый ответ модели: {result.FinishReason}");
    }

    private static Task WriteJsonAsync(string path, object? value) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, Json), new UTF8Encoding(false));

    private static string Usage(IEnumerable<ChatResult> calls, bool prompt)
    {
        var array = calls.ToArray();
        return array.All(c => c.PromptTokens > 0 || c.CompletionTokens > 0)
            ? array.Sum(c => prompt ? c.PromptTokens : c.CompletionTokens).ToString()
            : "н/д";
    }

    private static string BuildStatistics(Report report)
    {
        var full = report.Probes.Select(p => p.Full).ToArray();
        var compact = report.Probes.Select(p => p.Compressed).ToArray();
        var withOverhead = compact.Concat(report.SummaryCalls).ToArray();
        StringBuilder text = new();
        text.AppendLine($"\n| Метрика | Без сжатия | Со сжатием |\n|---|---:|---:|");
        text.AppendLine($"| Верных деталей | {report.Probes.Count(p => p.FullCorrect)}/{report.Probes.Count} | {report.Probes.Count(p => p.CompressedCorrect)}/{report.Probes.Count} |");
        text.AppendLine($"| История, локальная оценка токенов | {report.RawTokens} | {report.EffectiveTokens} |");
        text.AppendLine($"| Входные токены вопросов, API | {Usage(full, true)} | {Usage(compact, true)} |");
        text.AppendLine($"| Выходные токены вопросов, API | {Usage(full, false)} | {Usage(compact, false)} |");
        text.AppendLine($"| Входные токены summary, API | 0 | {Usage(report.SummaryCalls, true)} |");
        text.AppendLine($"| Выходные токены summary, API | 0 | {Usage(report.SummaryCalls, false)} |");
        text.AppendLine($"| Все входные токены сравнения | {Usage(full, true)} | {Usage(withOverhead, true)} |");
        text.AppendLine($"| Все выходные токены сравнения | {Usage(full, false)} | {Usage(withOverhead, false)} |");
        if (full.Concat(withOverhead).All(c => c.PromptTokens > 0 || c.CompletionTokens > 0))
        {
            int before = full.Sum(c => c.PromptTokens + c.CompletionTokens);
            int after = withOverhead.Sum(c => c.PromptTokens + c.CompletionTokens);
            text.AppendLine($"| Всего токенов с учётом summary | {before} | {after} |");
            text.AppendLine($"\nИзменение расхода с учётом summary: {before - after} токенов " +
                            $"({(before == 0 ? 0 : 100.0 * (before - after) / before):F1}% экономии).");
            if (report.DialogueCalls.All(c => c.PromptTokens > 0 || c.CompletionTokens > 0))
            {
                int preparation = report.DialogueCalls.Sum(c => c.PromptTokens + c.CompletionTokens);
                text.AppendLine($"Включая одинаковую подготовку диалога: без сжатия {before + preparation}, " +
                                $"со сжатием {after + preparation}; экономия " +
                                $"{(before + preparation == 0 ? 0 : 100.0 * (before - after) / (before + preparation)):F1}%.");
            }
        }
        text.AppendLine($"\nОбщая подготовка исходного диалога: {report.DialogueCalls.Count} API-вызовов, " +
                        $"вход {Usage(report.DialogueCalls, true)}, выход {Usage(report.DialogueCalls, false)}. " +
                        "Этот одинаковый для обоих режимов расход приведён отдельно и не включён в таблицу сравнения.");
        text.AppendLine($"\nSummary использовано: {report.UsesSummary}; обновлений: {report.SummaryCalls.Count}.");
        return text.ToString();
    }

    private static string BuildConclusion(Report report)
    {
        int fullCorrect = report.Probes.Count(p => p.FullCorrect);
        int compactCorrect = report.Probes.Count(p => p.CompressedCorrect);
        var lost = report.Probes.Where(p => p.FullCorrect && !p.CompressedCorrect).Select(p => p.Detail).ToArray();
        var failed = report.Probes.Where(p => !p.CompressedCorrect).Select(p => p.Detail).ToArray();
        StringBuilder text = new();
        text.AppendLine($"1. Память: без сжатия {fullCorrect}/{report.Probes.Count}, со сжатием {compactCorrect}/{report.Probes.Count} деталей.");
        text.AppendLine("2. Потеряно относительно полной истории: " + (lost.Length == 0 ? "не обнаружено." : string.Join(", ", lost) + "."));
        if (failed.Length > 0) text.AppendLine("Не пройдены вопросы со сжатием: " + string.Join(", ", failed) + ". Проверьте исходные ответы в отчёте.");
        var baseline = report.Probes.Select(p => p.Full).ToArray();
        var compressed = report.Probes.Select(p => p.Compressed).Concat(report.SummaryCalls).ToArray();
        if (baseline.Concat(compressed).All(c => c.PromptTokens > 0 || c.CompletionTokens > 0))
        {
            int before = baseline.Sum(c => c.PromptTokens + c.CompletionTokens);
            int after = compressed.Sum(c => c.PromptTokens + c.CompletionTokens);
            text.AppendLine($"3. Расход проверки с учётом ВСЕХ обновлений summary: {before} → {after} токенов.");
            text.AppendLine(before > after
                ? $"Сжатие окупилось: сэкономлено {before - after} токенов ({100.0 * (before - after) / before:F1}%)."
                : before == after ? "В этом прогоне расход одинаковый."
                : $"В этом прогоне сжатие ещё не окупилось: потрачено дополнительно {after - before} токенов.");
        }
        else text.AppendLine("3. API не вернул полный usage: общую экономию токенов установить нельзя.");
        text.AppendLine("Summary и последние сообщения уменьшают передаваемую историю; создание summary тоже требует токенов.");
        text.AppendLine("Это результат одного реального прогона. Проверка фактов лексическая; ответы доступны для ручной оценки.");
        return text.ToString();
    }

    private static string BuildMarkdown(Report report)
    {
        StringBuilder text = new();
        text.AppendLine($"# Day-9: сжатие истории\n\nДата UTC: {report.CreatedAt:O}; {report.Provider}, `{report.Model}`.");
        text.AppendLine($"\nОбщий диалог: {report.MessageCount} сообщений. keep={report.Keep}, batch={report.Batch}. " +
                        "Перед вопросами принудительно сжат оставшийся старый хвост.");
        text.AppendLine(BuildStatistics(report));
        text.AppendLine("\n| Деталь | Ожидалось | Без сжатия | Со сжатием |\n|---|---|---|---|");
        foreach (var p in report.Probes)
            text.AppendLine($"| {Cell(p.Detail)} | {Cell(p.Expected)} | {(p.FullCorrect ? "✓" : "✗")} {Cell(p.Full.Content)} | {(p.CompressedCorrect ? "✓" : "✗")} {Cell(p.Compressed.Content)} |");
        var lost = report.Probes.Where(p => p.FullCorrect && !p.CompressedCorrect).Select(p => p.Detail).ToArray();
        text.AppendLine($"\nПотеряно после сжатия относительно полного контекста: {(lost.Length == 0 ? "не обнаружено" : string.Join(", ", lost))}.");
        text.AppendLine("\nОдин запуск, одинаковая исходная история, вопросы независимы. Проверка ищет эталонные факты " +
                        "после удаления пробелов и пунктуации, проверяет границы чисел, а ДА/НЕТ сравнивает целиком. " +
                        "Для экспорта нужны оба факта: CSV и UTF-8. Проверка лексическая; все ответы сохранены для ручной оценки. " +
                        "Это проверка конкретного сценария, а не гарантия сохранения любых деталей.");
        text.AppendLine($"\n## Вывод\n\n{BuildConclusion(report)}");
        text.AppendLine($"\n## Итоговое summary\n\n{report.Summary}");
        return text.ToString();
    }

    private static string Cell(string value) => value.Replace("|", "\\|").Replace("\r", "").Replace("\n", "<br>");

    private sealed record ProbeResult(string Detail, string Expected, string Question,
        ChatResult Full, ChatResult Compressed, bool FullCorrect, bool CompressedCorrect);

    private sealed record Report(DateTimeOffset CreatedAt, string Provider, string Model,
        int MessageCount, int Keep, int Batch, int RawTokens, int EffectiveTokens, bool UsesSummary,
        string Summary, List<ProbeResult> Probes, List<ChatResult> DialogueCalls, List<ChatResult> SummaryCalls);
}
