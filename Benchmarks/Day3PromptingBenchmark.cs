using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using UniversalAI.Core;
using UniversalAI.Core.Capabilities;
using UniversalAI.Providers.OpenAICompatible;

namespace UniversalAI.Benchmarks;

public static class Day3PromptingBenchmark
{
    private const int MaxAnswerTokens = 1024;

    private static readonly IReadOnlyList<BenchmarkStrategy> Strategies =
    [
        new("direct", "Прямой ответ"),
        new("step_by_step", "Пошагово"),
        new("prompt_generator", "Промпт, составленный моделью"),
        new("expert_group", "Группа экспертов")
    ];

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Any(arg => arg.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
                            arg.Equals("-h", StringComparison.OrdinalIgnoreCase)))
        {
            PrintHelp();
            return 0;
        }

        try
        {
            OpenAICompatibleOptions providerOptions = OpenAICompatibleOptions.FromEnvironment();
            BenchmarkOptions options = ParseOptions(args, providerOptions);

            string? apiKey = Environment.GetEnvironmentVariable(
                options.ProviderOptions.ApiKeyEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException(
                    $"Не найдена переменная окружения API-ключа " +
                    $"'{options.ProviderOptions.ApiKeyEnvironmentVariable}'. " +
                    "Задайте её перед запуском эксперимента.");
            }

            using HttpClient http = new() { Timeout = TimeSpan.FromMinutes(3) };
            OpenAICompatibleProvider provider = new(http, options.ProviderOptions);
            provider.ValidateApiKey();

            ValidateEfforts(provider.Name, options.Model, options.LowEffort, options.HighEffort);

            Console.WriteLine("Эксперимент по стратегиям промптинга");
            Console.WriteLine($"Провайдер: {provider.Name}");
            Console.WriteLine($"Модель: {options.Model}");
            Console.WriteLine($"Задач: {Day3TaskSet.Tasks.Count}; запросов планируется примерно: {Day3TaskSet.Tasks.Count * 10}");
            Console.WriteLine("Порядок выполнения: все запросы последовательно.");
            Console.WriteLine();

            List<BenchmarkAggregate> aggregates = [];
            foreach (BenchmarkStrategy strategy in Strategies)
            {
                foreach (ReasoningSetting setting in new[]
                         {
                             new ReasoningSetting("low", options.LowEffort),
                             new ReasoningSetting("максимальное", options.HighEffort)
                         })
                {
                    List<BenchmarkCaseResult> cases = [];
                    for (int index = 0; index < Day3TaskSet.Tasks.Count; index++)
                    {
                        BenchmarkTask task = Day3TaskSet.Tasks[index];
                        BenchmarkCaseResult result = await RunCaseAsync(
                            provider,
                            options.Model,
                            strategy,
                            setting,
                            task);
                        cases.Add(result);

                        string status = result.Error != null
                            ? "ошибка"
                            : $"{(result.IsCorrect ? "верно" : "неверно")}, {result.TotalTokens} токенов, {result.ElapsedMilliseconds / 1000d:F1} с";
                        Console.WriteLine(
                            $"[{strategy.Label}; {setting.Label}] {index + 1}/{Day3TaskSet.Tasks.Count} " +
                            $"{task.Id}: {status}");
                    }

                    aggregates.Add(BenchmarkAggregate.From(strategy, setting, cases));
                }
            }

            BenchmarkReport report = new(
                DateTimeOffset.UtcNow,
                provider.Name,
                options.Model,
                options.ProviderOptions.BaseUrl,
                Day3TaskSet.Tasks.Count,
                aggregates);

            string csvPath = Path.GetFullPath(options.OutputPath);
            string markdownPath = Path.ChangeExtension(csvPath, ".md");
            string jsonPath = Path.ChangeExtension(csvPath, ".json");
            string? outputDirectory = Path.GetDirectoryName(csvPath);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            await File.WriteAllTextAsync(csvPath, BuildCsv(report), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            await File.WriteAllTextAsync(markdownPath, BuildMarkdown(report), new UTF8Encoding(false));
            await File.WriteAllTextAsync(
                jsonPath,
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));

            Console.WriteLine();
            Console.WriteLine(BuildMarkdown(report));
            Console.WriteLine($"CSV: {csvPath}");
            Console.WriteLine($"Markdown: {markdownPath}");
            Console.WriteLine($"Подробные ответы: {jsonPath}");

            return aggregates.Any(aggregate => aggregate.FailedTasks > 0) ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Не удалось запустить эксперимент: {ex.Message}");
            Console.Error.WriteLine("Справка: dotnet run -- --benchmark day3 --help");
            return 1;
        }
    }

    private static BenchmarkOptions ParseOptions(
        string[] args,
        OpenAICompatibleOptions defaults)
    {
        string model = defaults.DefaultModel;
        string? highEffort = null;
        string output = "benchmark-results-Day-3.csv";

        for (int index = 0; index < args.Length; index++)
        {
            string option = args[index];
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Для параметра {option} нужно указать значение.");
            }

            string value = args[++index];
            switch (option.ToLowerInvariant())
            {
                case "--provider":
                    defaults.Name = value;
                    break;
                case "--model":
                    model = value;
                    break;
                case "--base-url":
                    defaults.BaseUrl = value;
                    break;
                case "--api-key-env":
                    defaults.ApiKeyEnvironmentVariable = value;
                    break;
                case "--high-effort":
                    highEffort = value;
                    break;
                case "--output":
                    output = value;
                    break;
                default:
                    throw new ArgumentException($"Неизвестный параметр: {option}");
            }
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("Задайте модель параметром --model.");
        }

        if (string.IsNullOrWhiteSpace(defaults.BaseUrl))
        {
            throw new ArgumentException("Задайте адрес API параметром --base-url.");
        }

        string maximumEffort = highEffort ?? GetMaximumEffort(model);
        return new BenchmarkOptions(defaults, model, "low", maximumEffort, output);
    }

    private static string GetMaximumEffort(string model)
    {
        string normalized = model.Trim().ToLowerInvariant();
        return normalized is "deepseek/deepseek-v4.1-flash" or "gpt-6-luna" or "openai/gpt-6-luna"
            ? "max"
            : "high";
    }

    private static void ValidateEfforts(
        string providerName,
        string model,
        string lowEffort,
        string highEffort)
    {
        ModelCapabilities capabilities = ModelCapabilityResolver.Resolve(providerName, model);
        foreach (string effort in new[] { lowEffort, highEffort })
        {
            ChatRequestValidator.Validate(
                new ChatRequest { Model = model, ReasoningEffort = effort },
                capabilities);
        }
    }

    private static async Task<BenchmarkCaseResult> RunCaseAsync(
        OpenAICompatibleProvider provider,
        string model,
        BenchmarkStrategy strategy,
        ReasoningSetting setting,
        BenchmarkTask task)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        int tokens = 0;
        int promptTokens = 0;
        int completionTokens = 0;
        int apiCalls = 0;
        string? generatedPrompt = null;
        string rawAnswer = "";
        List<string> extractedAnswers = [];
        int correctExpertAnswers = 0;
        string? error = null;

        async Task<ChatResult> SendAsync(string content)
        {
            ChatResult result = await provider.SendMessageAsync(new ChatRequest
            {
                Model = model,
                Messages = [new ChatMessage { Role = "user", Content = content }],
                MaxTokens = MaxAnswerTokens,
                ReasoningEffort = setting.ApiEffort
            });

            apiCalls++;
            promptTokens += result.PromptTokens;
            completionTokens += result.CompletionTokens;
            tokens += result.TotalTokens > 0
                ? result.TotalTokens
                : result.PromptTokens + result.CompletionTokens;
            return result;
        }

        try
        {
            switch (strategy.Id)
            {
                case "direct":
                    rawAnswer = (await SendAsync(task.Question)).Content;
                    break;
                case "step_by_step":
                    rawAnswer = (await SendAsync(
                        $"{task.Question}\n\nРешай пошагово. В последней строке укажи только ответ в формате ANSWER: <значение>.")).Content;
                    break;
                case "prompt_generator":
                    string promptRequest =
                        "Составь краткую и ясную инструкцию-промпт для решения приведённой ниже задачи. " +
                        "Не решай задачу и не повторяй её условие. Инструкция должна требовать корректного решения " +
                        "и заканчивать ответ последней строкой ANSWER: <значение>. Верни только текст этой инструкции.\n\n" +
                        $"Задача:\n{task.Question}";
                    generatedPrompt = (await SendAsync(promptRequest)).Content.Trim();
                    rawAnswer = (await SendAsync(
                        $"{generatedPrompt}\n\nЗадача:\n{task.Question}")).Content;
                    break;
                case "expert_group":
                    string expertPrompt =
                        $"Решите задачу: {task.Question}\n\n" +
                        "Представьте три независимых мнения. Каждый эксперт должен дать отдельный раздел и последней строкой " +
                        "указать ответ строго как ANSWER: <значение>.\n" +
                        "АНАЛИТИК: проверь вычисления и закономерность.\n" +
                        "ИНЖЕНЕР: найди алгоритмическое или формальное решение.\n" +
                        "КРИТИК: проверь условие и возможные ошибки двух других подходов.";
                    rawAnswer = (await SendAsync(expertPrompt)).Content;
                    break;
                default:
                    throw new InvalidOperationException($"Неизвестная стратегия: {strategy.Id}");
            }

            extractedAnswers = BenchmarkAnswerScorer.ExtractAnswers(rawAnswer);
            if (extractedAnswers.Count == 0)
            {
                extractedAnswers = BenchmarkAnswerScorer.ExtractFinalNumericValues(rawAnswer, 1);
            }

            correctExpertAnswers = extractedAnswers.Count(
                answer => BenchmarkAnswerScorer.IsExpectedAnswer(answer, task.ExpectedAnswers));
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            stopwatch.Stop();
        }

        bool isCorrect = strategy.Id == "expert_group" && extractedAnswers.Count > 1
            ? correctExpertAnswers > extractedAnswers.Count / 2
            : extractedAnswers.Any(answer => BenchmarkAnswerScorer.IsExpectedAnswer(answer, task.ExpectedAnswers));

        return new BenchmarkCaseResult(
            task.Id,
            task.Question,
            task.ExpectedAnswers,
            isCorrect,
            correctExpertAnswers,
            extractedAnswers,
            tokens,
            promptTokens,
            completionTokens,
            apiCalls,
            stopwatch.ElapsedMilliseconds,
            generatedPrompt,
            rawAnswer,
            error);
    }

    private static string BuildCsv(BenchmarkReport report)
    {
        StringBuilder csv = new();
        csv.AppendLine("Способ;Рассуждение;Верно;Всего задач;Точность, %;Токены;Время, с;API-запросы;Ошибки");
        foreach (BenchmarkAggregate aggregate in report.Results)
        {
            csv.Append(EscapeCsv(aggregate.StrategyLabel)).Append(';')
                .Append(EscapeCsv(aggregate.ReasoningLabel)).Append(';')
                .Append(aggregate.CorrectTasks).Append(';')
                .Append(aggregate.TaskCount).Append(';')
                .Append(aggregate.AccuracyPercent.ToString("F1", CultureInfo.InvariantCulture)).Append(';')
                .Append(aggregate.TotalTokens).Append(';')
                .Append((aggregate.ElapsedMilliseconds / 1000d).ToString("F2", CultureInfo.InvariantCulture)).Append(';')
                .Append(aggregate.ApiCalls).Append(';')
                .AppendLine(aggregate.FailedTasks.ToString(CultureInfo.InvariantCulture));
        }

        return csv.ToString();
    }

    private static string BuildMarkdown(BenchmarkReport report)
    {
        StringBuilder markdown = new();
        markdown.AppendLine($"## Day-3: приёмы промптинга и reasoning — {report.Provider}, {report.Model}");
        markdown.AppendLine();
        markdown.AppendLine("| Способ | Рассуждение | Верно | Токены | Время, с | Запросы | Ошибки |");
        markdown.AppendLine("|---|---|---:|---:|---:|---:|---:|");
        foreach (BenchmarkAggregate aggregate in report.Results)
        {
            markdown.Append("| ").Append(aggregate.StrategyLabel).Append(" | ")
                .Append(aggregate.ReasoningLabel).Append(" | ")
                .Append(aggregate.CorrectTasks).Append('/').Append(aggregate.TaskCount).Append(" | ")
                .Append(aggregate.TotalTokens).Append(" | ")
                .Append((aggregate.ElapsedMilliseconds / 1000d).ToString("F2", CultureInfo.InvariantCulture)).Append(" | ")
                .Append(aggregate.ApiCalls).Append(" | ")
                .Append(aggregate.FailedTasks).AppendLine(" |");
        }

        markdown.AppendLine();
        markdown.AppendLine("### Вывод о пользе приёмов промптинга");
        markdown.AppendLine();
        markdown.AppendLine(BuildConclusions(report));
        markdown.AppendLine();
        markdown.AppendLine($"Время суммировано по отдельным запускам; дата UTC: {report.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss}.");
        markdown.AppendLine("Для группы экспертов точность задачи засчитывается, если правильный ответ дал строгий ответ большинства распознанных экспертов. Для остальных способов оценивается итоговый ответ. Токены берутся из usage API.");
        return markdown.ToString();
    }

    private static string BuildConclusions(BenchmarkReport report)
    {
        List<string> conclusions = [];
        BenchmarkAggregate? lowDirect = report.Results.FirstOrDefault(
            result => result.StrategyId == "direct" && result.ReasoningKey == "low");
        BenchmarkAggregate? maxDirect = report.Results.FirstOrDefault(
            result => result.StrategyId == "direct" && result.ReasoningKey == "максимальное");

        foreach (BenchmarkStrategy strategy in Strategies.Where(item => item.Id != "direct"))
        {
            BenchmarkAggregate? low = report.Results.FirstOrDefault(
                result => result.StrategyId == strategy.Id && result.ReasoningKey == "low");
            BenchmarkAggregate? maximum = report.Results.FirstOrDefault(
                result => result.StrategyId == strategy.Id && result.ReasoningKey == "максимальное");
            if (low is null || maximum is null || lowDirect is null || maxDirect is null)
            {
                continue;
            }

            int lowLift = low.CorrectTasks - lowDirect.CorrectTasks;
            int maximumLift = maximum.CorrectTasks - maxDirect.CorrectTasks;
            if (lowLift > 0 && maximumLift <= 0)
            {
                conclusions.Add(
                    $"- **{strategy.Label}**: прирост при low был +{lowLift} задач(и), " +
                    $"а при максимальном рассуждении исчез ({maximumLift:+#;-#;0}); " +
                    $"расход вырос на {maximum.TotalTokens - maxDirect.TotalTokens} токенов и " +
                    $"{(maximum.ElapsedMilliseconds - maxDirect.ElapsedMilliseconds) / 1000d:F2} с относительно прямого ответа.");
            }
            else if (maximumLift <= 0)
            {
                conclusions.Add(
                    $"- **{strategy.Label}**: при максимальном рассуждении точность не выше прямого ответа " +
                    $"({maximum.CorrectTasks}/{maximum.TaskCount} против {maxDirect.CorrectTasks}/{maxDirect.TaskCount}); " +
                    $"дополнительные токены: {maximum.TotalTokens - maxDirect.TotalTokens}.");
            }
            else
            {
                conclusions.Add(
                    $"- **{strategy.Label}**: при максимальном рассуждении сохраняется прирост " +
                    $"+{maximumLift} задач(и) к прямому ответу.");
            }
        }

        return conclusions.Count == 0
            ? "Недостаточно данных для сравнения; проверьте ошибки запросов в подробном JSON-файле."
            : string.Join(Environment.NewLine, conclusions);
    }

    private static string EscapeCsv(string value)
    {
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Запуск эксперимента:
              dotnet run -- --benchmark day3 [параметры]

            Параметры:
              --provider ИМЯ       имя OpenAI-совместимого провайдера (по умолчанию ProxyAPI)
              --model МОДЕЛЬ       ID модели (по умолчанию из ProxyAPI options)
              --base-url URL       базовый URL API
              --api-key-env ИМЯ    переменная окружения с API-ключом
              --high-effort УРОВЕНЬ уровень максимального рассуждения; по умолчанию max для DeepSeek V4.1 Flash / GPT-6 Luna, иначе high
              --output ПУТЬ        CSV-файл (по умолчанию benchmark-results-Day-3.csv); рядом создаются .md и .json
              --help               эта справка

            Нужно заранее задать переменную окружения API-ключа, например PROXYAPI_API_KEY.
            Для low и максимального уровня модель должна поддерживать reasoning_effort.
            """);
    }

    private sealed record BenchmarkOptions(
        OpenAICompatibleOptions ProviderOptions,
        string Model,
        string LowEffort,
        string HighEffort,
        string OutputPath);

    private sealed record BenchmarkStrategy(string Id, string Label);
    private sealed record ReasoningSetting(string Label, string ApiEffort);
    private sealed record BenchmarkCaseResult(
        string TaskId,
        string Question,
        IReadOnlyList<string> ExpectedAnswers,
        bool IsCorrect,
        int CorrectExpertAnswers,
        IReadOnlyList<string> ExtractedAnswers,
        int TotalTokens,
        int PromptTokens,
        int CompletionTokens,
        int ApiCalls,
        long ElapsedMilliseconds,
        string? GeneratedPrompt,
        string RawAnswer,
        string? Error);

    private sealed record BenchmarkAggregate(
        string StrategyId,
        string StrategyLabel,
        string ReasoningKey,
        string ReasoningLabel,
        int CorrectTasks,
        int TaskCount,
        double AccuracyPercent,
        int TotalTokens,
        long ElapsedMilliseconds,
        int ApiCalls,
        int FailedTasks,
        IReadOnlyList<BenchmarkCaseResult> Cases)
    {
        public static BenchmarkAggregate From(
            BenchmarkStrategy strategy,
            ReasoningSetting setting,
            IReadOnlyList<BenchmarkCaseResult> cases)
        {
            int correct = cases.Count(result => result.IsCorrect);
            string reasoningKey = setting.Label == "low" ? "low" : "максимальное";
            return new BenchmarkAggregate(
                strategy.Id,
                strategy.Label,
                reasoningKey,
                setting.Label == "low" ? "Низкое (low)" : $"Максимальное ({setting.ApiEffort})",
                correct,
                cases.Count,
                cases.Count == 0 ? 0 : correct * 100d / cases.Count,
                cases.Sum(result => result.TotalTokens),
                cases.Sum(result => result.ElapsedMilliseconds),
                cases.Sum(result => result.ApiCalls),
                cases.Count(result => result.Error != null),
                cases);
        }
    }

    private sealed record BenchmarkReport(
        DateTimeOffset GeneratedAtUtc,
        string Provider,
        string Model,
        string BaseUrl,
        int TaskCount,
        IReadOnlyList<BenchmarkAggregate> Results);
}
