using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UniversalAI.Core;
using UniversalAI.Core.Capabilities;
using UniversalAI.Providers.OpenAICompatible;

namespace UniversalAI.Benchmarks;

public static class Day4TemperatureBenchmark
{
    private const int Repetitions = 5;
    private const int MaxAnswerTokens = 400;

    private static readonly double[] Temperatures = [0, 0.7, 1.2];

    private static readonly Regex ExactAnswerFormat = new(
        @"^ANSWER\s*:\s*(?<answer>[-+]?\d+(?:[.,]\d+)?)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex NumberedIdeaFormat = new(
        @"^(?<number>[1-5])\.\s+(?<title>\S.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly IReadOnlyList<TemperatureTask> Tasks =
    [
        new(
            "single_answer",
            "Задача с одним правильным ответом",
            "Вычислите 17 × 19. Выведите ровно одну строку, строго в формате ANSWER: <число>. Не добавляйте пояснений.",
            ["323"],
            true),
        new(
            "generation",
            "Генерация вариантов названия",
            "Предложите ровно пять коротких оригинальных названий на русском языке для приложения, помогающего планировать личный бюджет. Выведите только пять строк в формате `1. Название`, `2. Название` и так далее до `5. Название`. Без вступления и пояснений.",
            [],
            false)
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
            TemperatureOptions options = ParseOptions(args, providerOptions);

            using HttpClient http = new() { Timeout = TimeSpan.FromMinutes(3) };
            OpenAICompatibleProvider provider = new(http, options.ProviderOptions);
            provider.ValidateApiKey();

            ModelCapabilities capabilities = ModelCapabilityResolver.Resolve(provider.Name, options.Model);
            if (!capabilities.SupportsTemperature)
            {
                throw new NotSupportedException(
                    $"Модель {options.Model} не поддерживает параметр temperature.");
            }

            foreach (double temperature in Temperatures)
            {
                ChatRequestValidator.Validate(
                    new ChatRequest
                    {
                        Model = options.Model,
                        MaxTokens = MaxAnswerTokens,
                        Temperature = temperature
                    },
                    capabilities);
            }

            Console.WriteLine("Day-4: влияние temperature на стабильность и разнообразие");
            Console.WriteLine($"Провайдер: {provider.Name}; модель: {options.Model}");
            Console.WriteLine($"Задач: {Tasks.Count}; повторов на комбинацию: {Repetitions}; API-запросов: {Tasks.Count * Temperatures.Length * Repetitions}");
            Console.WriteLine();

            List<TemperatureAggregate> aggregates = [];
            foreach (double temperature in Temperatures)
            {
                foreach (TemperatureTask task in Tasks)
                {
                    List<TemperatureCaseResult> cases = [];
                    for (int repetition = 1; repetition <= Repetitions; repetition++)
                    {
                        TemperatureCaseResult result = await RunCaseAsync(
                            provider,
                            options.Model,
                            task,
                            temperature,
                            repetition);
                        cases.Add(result);

                        string status = result.Error != null
                            ? "ошибка"
                            : $"формат {(result.FormatValid ? "OK" : "ошибка")}" +
                              (task.HasKnownAnswer
                                  ? $", ответ {(result.IsCorrect == true ? "верный" : "неверный")}" 
                                  : $", вариантов {result.Titles.Count}");
                        Console.WriteLine(
                            $"[temperature={temperature.ToString("0.0", CultureInfo.InvariantCulture)}; {task.Id}] " +
                            $"{repetition}/{Repetitions}: {status}");
                    }

                    aggregates.Add(TemperatureAggregate.From(task, temperature, cases));
                }
            }

            TemperatureReport report = new(
                DateTimeOffset.UtcNow,
                provider.Name,
                options.Model,
                options.ProviderOptions.BaseUrl,
                Repetitions,
                aggregates);

            string csvPath = Path.GetFullPath(options.OutputPath);
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
            Console.WriteLine($"Примеры и выводы: {markdownPath}");
            Console.WriteLine($"Все 30 ответов и метрики: {jsonPath}");

            return aggregates.Any(aggregate => aggregate.FailedCalls > 0) ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Не удалось запустить Day-4: {ex.Message}");
            Console.Error.WriteLine("Справка: dotnet run -- --benchmark day4 --help");
            return 1;
        }
    }

    private static TemperatureOptions ParseOptions(
        string[] args,
        OpenAICompatibleOptions providerOptions)
    {
        string model = "openai/gpt-4o-mini";
        string outputPath = "benchmark-results-Day-4.csv";

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
                    providerOptions.Name = value;
                    break;
                case "--model":
                    model = value;
                    break;
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

        return new TemperatureOptions(providerOptions, model, outputPath);
    }

    private static async Task<TemperatureCaseResult> RunCaseAsync(
        OpenAICompatibleProvider provider,
        string model,
        TemperatureTask task,
        double temperature,
        int repetition)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        string rawAnswer = "";
        int promptTokens = 0;
        int completionTokens = 0;
        int totalTokens = 0;
        List<string> titles = [];
        string? answerValue = null;
        bool formatValid = false;
        bool? isCorrect = task.HasKnownAnswer ? false : null;
        string? error = null;

        try
        {
            ChatResult response = await provider.SendMessageAsync(new ChatRequest
            {
                Model = model,
                Messages = [new ChatMessage { Role = "user", Content = task.Prompt }],
                MaxTokens = MaxAnswerTokens,
                Temperature = temperature
            });

            rawAnswer = response.Content;
            promptTokens = response.PromptTokens;
            completionTokens = response.CompletionTokens;
            totalTokens = response.TotalTokens > 0
                ? response.TotalTokens
                : promptTokens + completionTokens;

            if (task.HasKnownAnswer)
            {
                string trimmed = rawAnswer.Trim();
                string[] lines = trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                Match match = lines.Length == 1
                    ? ExactAnswerFormat.Match(lines[0].TrimEnd('\r'))
                    : Match.Empty;
                formatValid = match.Success;

                List<string> extracted = BenchmarkAnswerScorer.ExtractAnswers(trimmed);
                if (extracted.Count == 0)
                {
                    extracted = BenchmarkAnswerScorer.ExtractFinalNumericValues(trimmed, 1);
                }

                answerValue = extracted.LastOrDefault();
                isCorrect = extracted.Any(answer =>
                    BenchmarkAnswerScorer.IsExpectedAnswer(answer, task.ExpectedAnswers));
            }
            else
            {
                titles = ParseTitles(rawAnswer, out formatValid);
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

        return new TemperatureCaseResult(
            task.Id,
            task.Label,
            task.HasKnownAnswer,
            temperature,
            repetition,
            formatValid,
            isCorrect,
            answerValue,
            titles,
            promptTokens,
            completionTokens,
            totalTokens,
            stopwatch.ElapsedMilliseconds,
            rawAnswer,
            error);
    }

    private static List<string> ParseTitles(string content, out bool formatValid)
    {
        string[] lines = content.Trim()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().TrimEnd('\r'))
            .ToArray();

        List<string> titles = [];
        bool valid = lines.Length == 5;
        for (int index = 0; index < lines.Length; index++)
        {
            Match match = NumberedIdeaFormat.Match(lines[index]);
            if (!match.Success || !int.TryParse(match.Groups["number"].Value, out int number) || number != index + 1)
            {
                valid = false;
                continue;
            }

            titles.Add(match.Groups["title"].Value.Trim());
        }

        formatValid = valid && titles.Count == 5;
        return titles;
    }

    private static string BuildCsv(TemperatureReport report)
    {
        StringBuilder csv = new();
        csv.AppendLine("Temperature;Тип задачи;Правильно;Неверных ответов;Сбоев формата;Ошибок API;Разных текстов;Разных числовых ответов;Разных названий;Токены;Время суммарно, с;Время среднее, с");
        foreach (TemperatureAggregate aggregate in report.Results)
        {
            csv.Append(aggregate.Temperature.ToString("0.0", CultureInfo.InvariantCulture)).Append(';')
                .Append(EscapeCsv(aggregate.TaskLabel)).Append(';')
                .Append(aggregate.CorrectCount?.ToString(CultureInfo.InvariantCulture) ?? "н/д").Append(';')
                .Append(aggregate.WrongAnswerCount?.ToString(CultureInfo.InvariantCulture) ?? "н/д").Append(';')
                .Append(aggregate.FormatFailures).Append(';')
                .Append(aggregate.FailedCalls).Append(';')
                .Append(aggregate.DistinctOutputs).Append(';')
                .Append(aggregate.DistinctAnswers).Append(';')
                .Append(aggregate.UniqueTitles).Append(';')
                .Append(aggregate.TotalTokens).Append(';')
                .Append((aggregate.ElapsedMilliseconds / 1000d).ToString("F2", CultureInfo.InvariantCulture)).Append(';')
                .AppendLine((aggregate.AverageMilliseconds / 1000d).ToString("F2", CultureInfo.InvariantCulture));
        }

        return csv.ToString();
    }

    private static string BuildMarkdown(TemperatureReport report)
    {
        StringBuilder markdown = new();
        markdown.AppendLine("## Day-4: результаты temperature");
        markdown.AppendLine();
        markdown.AppendLine($"Модель: `{report.Model}` через {report.Provider}. Каждый из двух запросов отправлен {report.Repetitions} раз для каждой температуры.");
        markdown.AppendLine();
        markdown.AppendLine("| Temperature | Тип задачи | Верно | Неверно | Сбои формата | Ошибки API | Разных текстов | Разных числовых ответов | Разных названий | Токены | Время всего / среднее, с |");
        markdown.AppendLine("|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (TemperatureAggregate result in report.Results)
        {
            markdown.Append("| ").Append(result.Temperature.ToString("0.0", CultureInfo.InvariantCulture)).Append(" | ")
                .Append(result.TaskLabel).Append(" | ")
                .Append(result.CorrectCount?.ToString(CultureInfo.InvariantCulture) ?? "н/д").Append('/').Append(report.Repetitions).Append(" | ")
                .Append(result.WrongAnswerCount?.ToString(CultureInfo.InvariantCulture) ?? "н/д").Append(" | ")
                .Append(result.FormatFailures).Append('/').Append(report.Repetitions).Append(" | ")
                .Append(result.FailedCalls).Append(" | ")
                .Append(result.DistinctOutputs).Append('/').Append(result.SuccessfulCalls).Append(" | ")
                .Append(result.DistinctAnswers).Append(" | ")
                .Append(result.UniqueTitles).Append(" | ")
                .Append(result.TotalTokens).Append(" | ")
                .Append((result.ElapsedMilliseconds / 1000d).ToString("F2", CultureInfo.InvariantCulture)).Append(" / ")
                .Append((result.AverageMilliseconds / 1000d).ToString("F2", CultureInfo.InvariantCulture)).AppendLine(" |");
        }

        markdown.AppendLine();
        markdown.AppendLine("### Выводы");
        markdown.AppendLine();
        markdown.AppendLine(BuildConclusions(report));
        markdown.AppendLine();
        markdown.AppendLine("### Примеры ответов");
        markdown.AppendLine();
        foreach (TemperatureAggregate result in report.Results)
        {
            TemperatureCaseResult? sample = result.Cases.FirstOrDefault(item => item.Error == null)
                ?? result.Cases.FirstOrDefault();
            markdown.AppendLine($"**temperature={result.Temperature.ToString("0.0", CultureInfo.InvariantCulture)} — {result.TaskLabel}**");
            markdown.AppendLine();
            if (sample is null || string.IsNullOrWhiteSpace(sample.RawAnswer))
            {
                markdown.AppendLine("Ответ отсутствует.");
            }
            else
            {
                foreach (string line in sample.RawAnswer.Trim().Split('\n'))
                {
                    markdown.Append("> ").AppendLine(line.TrimEnd('\r'));
                }
            }

            markdown.AppendLine();
        }

        markdown.AppendLine("Полные ответы всех повторов находятся в JSON. Разнообразие считается по числу уникальных ответов после нормализации пробелов; для задачи на названия дополнительно считается число уникальных вариантов.");
        markdown.AppendLine($"Дата прогона UTC: {report.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss}.");
        return markdown.ToString();
    }

    private static string BuildConclusions(TemperatureReport report)
    {
        List<string> conclusions = [];
        TemperatureAggregate? deterministicAtZero = report.Results.FirstOrDefault(
            item => item.Temperature == 0 && item.TaskId == "single_answer");
        if (deterministicAtZero != null)
        {
            bool sameEveryTime = deterministicAtZero.SuccessfulCalls == report.Repetitions &&
                                 deterministicAtZero.DistinctOutputs == 1;
            string answerText = sameEveryTime
                ? "все пять текстов совпали после нормализации регистра и пробелов"
                : $"получено {deterministicAtZero.DistinctOutputs} различных текстовых ответов из {deterministicAtZero.SuccessfulCalls} успешных вызовов";
            conclusions.Add($"- При temperature=0 задача с одним ответом: {deterministicAtZero.CorrectCount}/{report.Repetitions} верных, {deterministicAtZero.FormatFailures} сбоев формата; {answerText}.");
        }

        TemperatureAggregate[] creative = report.Results
            .Where(item => item.TaskId == "generation")
            .OrderBy(item => item.Temperature)
            .ToArray();
        if (creative.Length > 0)
        {
            TemperatureAggregate preferred = creative
                .OrderBy(item => item.FormatFailures)
                .ThenByDescending(item => item.UniqueTitles)
                .ThenBy(item => item.Temperature == 0.7 ? 0 : 1)
                .First();
            conclusions.Add(
                $"- Для генерации в этих пяти прогонах лучше сработала temperature={preferred.Temperature.ToString("0.0", CultureInfo.InvariantCulture)}: " +
                $"{preferred.UniqueTitles} разных названий при {preferred.FormatFailures} сбоях формата. Рекомендация выбирает больше вариантов и при равенстве предпочитает среднее значение 0.7.");

            TemperatureAggregate? zeroCreative = creative.FirstOrDefault(item => item.Temperature == 0);
            if (zeroCreative != null && preferred.UniqueTitles > zeroCreative.UniqueTitles)
            {
                conclusions.Add("- Повышение temperature увеличило разнообразие названий относительно temperature=0; для генерации вариантов это полезно, пока сохраняется требуемый формат.");
            }

            TemperatureAggregate? highCreative = creative.FirstOrDefault(item => item.Temperature == 1.2);
            if (highCreative != null && zeroCreative != null &&
                highCreative.UniqueTitles > zeroCreative.UniqueTitles &&
                highCreative.FormatFailures > zeroCreative.FormatFailures)
            {
                conclusions.Add(
                    $"- При temperature=1.2 названий стало больше ({highCreative.UniqueTitles} против {zeroCreative.UniqueTitles}), " +
                    $"но и сбоев формата больше ({highCreative.FormatFailures} против {zeroCreative.FormatFailures}); " +
                    "такое разнообразие требует ручной проверки.");
            }
            else if (zeroCreative != null && preferred.UniqueTitles <= zeroCreative.UniqueTitles)
            {
                conclusions.Add("- На генеративной задаче более высокая temperature не увеличила число уникальных названий в этом прогоне.");
            }
        }

        TemperatureAggregate[] exact = report.Results
            .Where(item => item.TaskId == "single_answer")
            .OrderBy(item => item.Temperature)
            .ToArray();
        if (exact.Length > 0)
        {
            TemperatureAggregate preferredExact = exact
                .OrderBy(item => item.CorrectCount.HasValue ? report.Repetitions - item.CorrectCount.Value : report.Repetitions)
                .ThenBy(item => item.FormatFailures)
                .ThenBy(item => item.Temperature)
                .First();
            conclusions.Add(
                $"- Для задачи с проверяемым ответом выбирайте temperature={preferredExact.Temperature.ToString("0.0", CultureInfo.InvariantCulture)}: " +
                $"{preferredExact.CorrectCount}/{report.Repetitions} правильных ответов и {preferredExact.FormatFailures} сбоев формата. При равной точности выбрано наименьшее значение как наиболее стабильное.");

            TemperatureAggregate? zeroExact = exact.FirstOrDefault(item => item.Temperature == 0);
            TemperatureAggregate? highExact = exact.FirstOrDefault(item => item.Temperature == 1.2);
            if (zeroExact != null && highExact != null &&
                (highExact.WrongAnswerCount > zeroExact.WrongAnswerCount ||
                 highExact.FormatFailures > zeroExact.FormatFailures))
            {
                conclusions.Add(
                    $"- При temperature=1.2 точный ответ менее надёжен, чем при temperature=0: " +
                    $"неверных ответов {highExact.WrongAnswerCount} против {zeroExact.WrongAnswerCount}, " +
                    $"сбоев формата {highExact.FormatFailures} против {zeroExact.FormatFailures}.");
            }
        }

        return conclusions.Count == 0
            ? "Недостаточно успешных ответов для выводов; проверьте ошибки в JSON."
            : string.Join(Environment.NewLine, conclusions);
    }

    private static string Normalize(string value) =>
        Regex.Replace(value.Trim(), @"\s+", " ").ToLowerInvariant();

    private static string NormalizeTitle(string value) =>
        Normalize(value.Trim().Trim('.', '"', '\'', '«', '»'));

    private static string EscapeCsv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Day-4 сравнивает temperature 0, 0.7 и 1.2 на двух типах задач.

            Запуск:
              dotnet run -- --benchmark day4

            Параметры:
              --provider ИМЯ       OpenAI-совместимый провайдер (по умолчанию ProxyAPI)
              --model МОДЕЛЬ       модель с поддержкой temperature (по умолчанию openai/gpt-4o-mini)
              --base-url URL       адрес API
              --api-key-env ИМЯ    переменная окружения API-ключа
              --output ПУТЬ        CSV (по умолчанию benchmark-results-Day-4.csv); рядом создаются .md и .json
              --help               эта справка

            Выполняется 30 API-запросов: 3 значения temperature × 2 задачи × 5 повторов.
            """);
    }

    private sealed record TemperatureOptions(
        OpenAICompatibleOptions ProviderOptions,
        string Model,
        string OutputPath);

    private sealed record TemperatureTask(
        string Id,
        string Label,
        string Prompt,
        IReadOnlyList<string> ExpectedAnswers,
        bool HasKnownAnswer);

    private sealed record TemperatureCaseResult(
        string TaskId,
        string TaskLabel,
        bool HasKnownAnswer,
        double Temperature,
        int Repetition,
        bool FormatValid,
        bool? IsCorrect,
        string? AnswerValue,
        IReadOnlyList<string> Titles,
        int PromptTokens,
        int CompletionTokens,
        int TotalTokens,
        long ElapsedMilliseconds,
        string RawAnswer,
        string? Error);

    private sealed record TemperatureAggregate(
        string TaskId,
        string TaskLabel,
        double Temperature,
        int? CorrectCount,
        int? WrongAnswerCount,
        int FormatFailures,
        int FailedCalls,
        int SuccessfulCalls,
        int DistinctOutputs,
        int DistinctAnswers,
        int UniqueTitles,
        int TotalTokens,
        long ElapsedMilliseconds,
        double AverageMilliseconds,
        IReadOnlyList<TemperatureCaseResult> Cases)
    {
        public static TemperatureAggregate From(
            TemperatureTask task,
            double temperature,
            IReadOnlyList<TemperatureCaseResult> cases)
        {
            TemperatureCaseResult[] successful = cases.Where(item => item.Error == null).ToArray();
            int? correct = task.HasKnownAnswer
                ? successful.Count(item => item.IsCorrect == true)
                : null;
            int? wrong = task.HasKnownAnswer
                ? successful.Count(item => item.IsCorrect != true)
                : null;
            int distinctOutputs = successful
                .Select(item => Normalize(item.RawAnswer))
                .Where(output => output.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Count();
            int distinctAnswers = successful
                .Where(item => !string.IsNullOrWhiteSpace(item.AnswerValue))
                .Select(item => Normalize(item.AnswerValue!))
                .Distinct(StringComparer.Ordinal)
                .Count();
            int uniqueTitles = successful
                .SelectMany(item => item.Titles)
                .Select(NormalizeTitle)
                .Where(title => title.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Count();

            return new TemperatureAggregate(
                task.Id,
                task.Label,
                temperature,
                correct,
                wrong,
                cases.Count(item => item.Error == null && !item.FormatValid),
                cases.Count(item => item.Error != null),
                successful.Length,
                distinctOutputs,
                distinctAnswers,
                uniqueTitles,
                cases.Sum(item => item.TotalTokens),
                cases.Sum(item => item.ElapsedMilliseconds),
                cases.Count == 0 ? 0 : cases.Average(item => (double)item.ElapsedMilliseconds),
                cases);
        }
    }

    private sealed record TemperatureReport(
        DateTimeOffset GeneratedAtUtc,
        string Provider,
        string Model,
        string BaseUrl,
        int Repetitions,
        IReadOnlyList<TemperatureAggregate> Results);
}
