using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using UniversalAI.Core;
using UniversalAI.Core.Capabilities;
using UniversalAI.Providers.OpenAICompatible;

namespace UniversalAI.Benchmarks;

public static class Day5ModelComparisonBenchmark
{
    private const string PriceSource = "https://proxyapi.ru/models/browse";
    private const string PriceSnapshot = "2026-11-08";
    private const int MaxAnswerTokens = 1024;

    private static readonly IReadOnlyList<ModelUnderTest> Models =
    [
        new("Бюджетная", "deepseek/deepseek-v4.1-flash", 26.07m, 100.25m),
        new("Средняя", "openai/gpt-6-sol", 420m, 2100m),
        new("Флагманская", "openai/gpt-6-astra", 1580m, 7900m)
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
            string outputPath = ParseOptions(args, providerOptions);
            if (providerOptions.Name.Equals("GigaChat", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException(
                    "Day-5 использует три модели ProxyAPI, поэтому нужен OpenAI-совместимый провайдер.");
            }

            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                    providerOptions.ApiKeyEnvironmentVariable)))
            {
                throw new InvalidOperationException(
                    $"Не найдена переменная окружения API-ключа " +
                    $"'{providerOptions.ApiKeyEnvironmentVariable}'.");
            }

            using HttpClient http = new() { Timeout = TimeSpan.FromMinutes(3) };
            OpenAICompatibleProvider provider = new(http, providerOptions);
            provider.ValidateApiKey();

            foreach (ModelUnderTest model in Models)
            {
                ModelCapabilities capabilities = ModelCapabilityResolver.Resolve(provider.Name, model.ModelId);
                ChatRequestValidator.Validate(
                    new ChatRequest { Model = model.ModelId, MaxTokens = MaxAnswerTokens },
                    capabilities);
            }

            Console.WriteLine("Day-5: сравнение моделей на наборе задач Day-3");
            Console.WriteLine($"Провайдер: {provider.Name}");
            Console.WriteLine($"Задач: {Day3TaskSet.Tasks.Count}; моделей: {Models.Count}; API-запросов: {Day3TaskSet.Tasks.Count * Models.Count}");
            Console.WriteLine($"Цены ProxyAPI зафиксированы на {PriceSnapshot} (руб. за 1 млн токенов).");
            Console.WriteLine();

            List<ModelAggregate> aggregates = [];
            foreach (ModelUnderTest model in Models)
            {
                List<Day5CaseResult> cases = [];
                for (int index = 0; index < Day3TaskSet.Tasks.Count; index++)
                {
                    BenchmarkTask task = Day3TaskSet.Tasks[index];
                    Day5CaseResult result = await RunCaseAsync(provider, model, task);
                    cases.Add(result);

                    string status = result.Error != null
                        ? "ошибка"
                        : $"{(result.IsCorrect ? "верно" : "неверно")}, {result.TotalTokens} токенов, {result.ElapsedMilliseconds / 1000d:F1} с";
                    Console.WriteLine($"[{model.Tier}] {index + 1}/{Day3TaskSet.Tasks.Count} {task.Id}: {status}");
                }

                aggregates.Add(ModelAggregate.From(model, cases));
            }

            Day5Report report = new(
                DateTimeOffset.UtcNow,
                provider.Name,
                providerOptions.BaseUrl,
                PriceSnapshot,
                PriceSource,
                Day3TaskSet.Tasks.Count,
                aggregates);

            string csvPath = Path.GetFullPath(outputPath);
            string markdownPath = Path.ChangeExtension(csvPath, ".md");
            string jsonPath = Path.ChangeExtension(csvPath, ".json");
            string? directory = Path.GetDirectoryName(csvPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(csvPath, BuildCsv(report), new UTF8Encoding(true));
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
            Console.Error.WriteLine($"Не удалось запустить Day-5: {ex.Message}");
            Console.Error.WriteLine("Справка: dotnet run -- --benchmark day5 --help");
            return 1;
        }
    }

    private static string ParseOptions(string[] args, OpenAICompatibleOptions providerOptions)
    {
        string outputPath = "benchmark-results-Day-5.csv";
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
                case "--base-url":
                    providerOptions.BaseUrl = value;
                    break;
                case "--api-key-env":
                    providerOptions.ApiKeyEnvironmentVariable = value;
                    break;
                case "--output":
                    outputPath = value;
                    break;
                default:
                    throw new ArgumentException($"Неизвестный параметр: {option}");
            }
        }

        return outputPath;
    }

    private static async Task<Day5CaseResult> RunCaseAsync(
        OpenAICompatibleProvider provider,
        ModelUnderTest model,
        BenchmarkTask task)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        int promptTokens = 0;
        int completionTokens = 0;
        int totalTokens = 0;
        string rawAnswer = "";
        List<string> extractedAnswers = [];
        string? error = null;

        try
        {
            ChatResult response = await provider.SendMessageAsync(new ChatRequest
            {
                Model = model.ModelId,
                Messages = [new ChatMessage { Role = "user", Content = task.Question }],
                MaxTokens = MaxAnswerTokens
            });

            promptTokens = response.PromptTokens;
            completionTokens = response.CompletionTokens;
            totalTokens = response.TotalTokens > 0
                ? response.TotalTokens
                : promptTokens + completionTokens;
            rawAnswer = response.Content;

            extractedAnswers = BenchmarkAnswerScorer.ExtractAnswers(rawAnswer);
            if (extractedAnswers.Count == 0)
            {
                extractedAnswers = BenchmarkAnswerScorer.ExtractFinalNumericValues(rawAnswer, 1);
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            stopwatch.Stop();
        }

        bool isCorrect = extractedAnswers.Any(answer =>
            BenchmarkAnswerScorer.IsExpectedAnswer(answer, task.ExpectedAnswers));
        decimal? costRubles = promptTokens + completionTokens > 0
            ? (promptTokens * model.InputRublesPerMillion + completionTokens * model.OutputRublesPerMillion) / 1_000_000m
            : null;

        return new Day5CaseResult(
            task.Id,
            task.Question,
            task.ExpectedAnswers,
            isCorrect,
            extractedAnswers,
            promptTokens,
            completionTokens,
            totalTokens,
            costRubles,
            stopwatch.ElapsedMilliseconds,
            rawAnswer,
            error);
    }

    private static string BuildCsv(Day5Report report)
    {
        StringBuilder csv = new();
        csv.AppendLine("Модель;Категория;Верно;Всего задач;Точность, %;Токены;Время суммарно, с;Время среднее, с/задачу;Расчётная стоимость, ₽;Вход, ₽/1М;Выход, ₽/1М;Ошибки");
        foreach (ModelAggregate result in report.Results)
        {
            csv.Append(EscapeCsv(result.ModelId)).Append(';')
                .Append(EscapeCsv(result.Tier)).Append(';')
                .Append(result.CorrectTasks).Append(';')
                .Append(result.TaskCount).Append(';')
                .Append(result.AccuracyPercent.ToString("F1", CultureInfo.InvariantCulture)).Append(';')
                .Append(result.TotalTokens).Append(';')
                .Append((result.ElapsedMilliseconds / 1000d).ToString("F2", CultureInfo.InvariantCulture)).Append(';')
                .Append((result.AverageMilliseconds / 1000d).ToString("F2", CultureInfo.InvariantCulture)).Append(';')
                .Append(result.CostRubles?.ToString("F6", CultureInfo.InvariantCulture) ?? "").Append(';')
                .Append(result.InputRublesPerMillion.ToString(CultureInfo.InvariantCulture)).Append(';')
                .Append(result.OutputRublesPerMillion.ToString(CultureInfo.InvariantCulture)).Append(';')
                .AppendLine(result.FailedTasks.ToString(CultureInfo.InvariantCulture));
        }

        return csv.ToString();
    }

    private static string BuildMarkdown(Day5Report report)
    {
        StringBuilder markdown = new();
        markdown.AppendLine("## Day-5: качество, скорость и стоимость моделей");
        markdown.AppendLine();
        markdown.AppendLine($"Провайдер: {report.Provider}; модельные запросы изолированы, каждый получает только условие одной задачи. Набор задач: Day-3 ({report.TaskCount}).");
        markdown.AppendLine();
        markdown.AppendLine("| Модель | Категория | Верно | Время всего / среднее, с | Токены | Расчётная стоимость, ₽ | Ошибки |");
        markdown.AppendLine("|---|---|---:|---:|---:|---:|---:|");
        foreach (ModelAggregate result in report.Results)
        {
            string cost = result.CostRubles?.ToString("F6", CultureInfo.InvariantCulture) ?? "н/д";
            markdown.Append("| `").Append(result.ModelId).Append("` | ").Append(result.Tier).Append(" | ")
                .Append(result.CorrectTasks).Append('/').Append(result.TaskCount).Append(" | ")
                .Append((result.ElapsedMilliseconds / 1000d).ToString("F2", CultureInfo.InvariantCulture)).Append(" / ")
                .Append((result.AverageMilliseconds / 1000d).ToString("F2", CultureInfo.InvariantCulture)).Append(" | ")
                .Append(result.TotalTokens).Append(" | ").Append(cost).Append(" | ")
                .Append(result.FailedTasks).AppendLine(" |");
        }

        markdown.AppendLine();
        markdown.AppendLine("### Сравнение и где достаточно бюджетной модели");
        markdown.AppendLine();
        markdown.AppendLine(BuildConclusions(report));
        markdown.AppendLine();
        markdown.AppendLine($"Расчётная стоимость рассчитана по prompt/completion tokens из ответа API и снимку стандартных ставок ProxyAPI на {report.PriceSnapshot}: ввод и вывод тарифицируются отдельно. Это оценка по прайс-листу, не выписка фактического списания; скидки за кэшированные токены не учитываются. Источник: [{PriceSource}]({report.PriceSource}). Если API не вернул usage, стоимость отмечена н/д. Цены могут измениться после даты снимка.");
        markdown.AppendLine($"Время суммировано по вызовам модели; среднее рассчитано на задачу. Дата прогона UTC: {report.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss}.");
        markdown.AppendLine("Уровень reasoning не задаётся отдельно: сравниваются модели на их настройках API по умолчанию. API может учитывать reasoning-токены в completion usage.");
        return markdown.ToString();
    }

    private static string BuildConclusions(Day5Report report)
    {
        ModelAggregate small = report.Results[0];
        ModelAggregate? flagship = report.Results.FirstOrDefault(model => model.Tier == "Флагманская");
        ModelAggregate fastest = report.Results.OrderBy(model => model.AverageMilliseconds).First();
        List<string> conclusions =
        [
            $"- Быстрее всех в этом прогоне: **{fastest.ModelId}** ({fastest.AverageMilliseconds / 1000d:F2} с/задачу)."
        ];

        if (flagship != null)
        {
            int accuracyDelta = flagship.CorrectTasks - small.CorrectTasks;
            string deltaText = accuracyDelta > 0
                ? $"на {accuracyDelta} задачу точнее"
                : accuracyDelta < 0
                    ? $"на {-accuracyDelta} задач(и) менее точен"
                    : "решил столько же задач верно";

            string costRatio = small.CostRubles is > 0 && flagship.CostRubles.HasValue
                ? $"Флагманская модель обошлась в {flagship.CostRubles.Value / small.CostRubles.Value:F2} раза дороже бюджетной"
                : "Сравнить стоимость флагманской и бюджетной модели нельзя из-за отсутствующих usage-данных";

            conclusions.Add(
                $"- **{flagship.ModelId}** {deltaText} ({flagship.CorrectTasks}/{flagship.TaskCount} против {small.CorrectTasks}/{small.TaskCount}); {costRatio}.");
        }

        BenchmarkTask[] enoughTasks = Day3TaskSet.Tasks
            .Where(task => small.Cases.Any(result =>
                result.TaskId == task.Id && result.Error == null && result.IsCorrect))
            .ToArray();
        if (enoughTasks.Length == 0)
        {
            conclusions.Add("- Бюджетная модель не решила верно ни одну задачу в этом прогоне.");
        }
        else
        {
            string taskList = string.Join("; ", enoughTasks.Select(task => $"`{task.Id}` — {task.Question}"));
            conclusions.Add($"- По точности бюджетной модели достаточно на задачах: {taskList}.");
        }

        return string.Join(Environment.NewLine, conclusions);
    }

    private static string EscapeCsv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Day-5 сравнивает фиксированный набор моделей ProxyAPI на тех же задачах Day-3.

            Запуск:
              dotnet run -- --benchmark day5

            Параметры:
              --base-url URL       адрес OpenAI-совместимого API
              --api-key-env ИМЯ    переменная окружения с API-ключом
              --output ПУТЬ        CSV (по умолчанию benchmark-results-Day-5.csv); рядом создаются .md и .json
              --help               эта справка

            Модели:
              deepseek/deepseek-v4.1-flash — бюджетная
              openai/gpt-6-sol             — средняя
              openai/gpt-6-astra           — флагманская

            """);
    }

    private sealed record ModelUnderTest(
        string Tier,
        string ModelId,
        decimal InputRublesPerMillion,
        decimal OutputRublesPerMillion);

    private sealed record Day5CaseResult(
        string TaskId,
        string Question,
        IReadOnlyList<string> ExpectedAnswers,
        bool IsCorrect,
        IReadOnlyList<string> ExtractedAnswers,
        int PromptTokens,
        int CompletionTokens,
        int TotalTokens,
        decimal? CostRubles,
        long ElapsedMilliseconds,
        string RawAnswer,
        string? Error);

    private sealed record ModelAggregate(
        string Tier,
        string ModelId,
        decimal InputRublesPerMillion,
        decimal OutputRublesPerMillion,
        int CorrectTasks,
        int TaskCount,
        double AccuracyPercent,
        int TotalTokens,
        long ElapsedMilliseconds,
        double AverageMilliseconds,
        decimal? CostRubles,
        int FailedTasks,
        IReadOnlyList<Day5CaseResult> Cases)
    {
        public static ModelAggregate From(
            ModelUnderTest model,
            IReadOnlyList<Day5CaseResult> cases)
        {
            int correct = cases.Count(result => result.IsCorrect);
            bool hasUsageForAll = cases.All(result => result.CostRubles.HasValue);
            decimal? cost = hasUsageForAll
                ? cases.Sum(result => result.CostRubles!.Value)
                : null;

            return new ModelAggregate(
                model.Tier,
                model.ModelId,
                model.InputRublesPerMillion,
                model.OutputRublesPerMillion,
                correct,
                cases.Count,
                cases.Count == 0 ? 0 : correct * 100d / cases.Count,
                cases.Sum(result => result.TotalTokens),
                cases.Sum(result => result.ElapsedMilliseconds),
                cases.Count == 0 ? 0 : cases.Average(result => (double)result.ElapsedMilliseconds),
                cost,
                cases.Count(result => result.Error != null),
                cases);
        }
    }

    private sealed record Day5Report(
        DateTimeOffset GeneratedAtUtc,
        string Provider,
        string BaseUrl,
        string PriceSnapshot,
        string PriceSource,
        int TaskCount,
        IReadOnlyList<ModelAggregate> Results);
}
