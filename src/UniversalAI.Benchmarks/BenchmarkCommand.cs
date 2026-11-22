namespace UniversalAI.Benchmarks;

/// <summary>Single CLI entry point and registry for all benchmark runs.</summary>
public static class BenchmarkCommand
{
    private static readonly BenchmarkEntry[] Entries =
    [
        new("day1", "Первый запрос к LLM через API", FoundationBenchmarks.HelpAsync),
        new("day2", "Формат ответа, лимит, stop и строгая JSON Schema", FoundationBenchmarks.HelpAsync),
        new("day3", "Приёмы промптинга и уровни рассуждения", Day3PromptingBenchmark.RunAsync),
        new("day4", "Влияние temperature на повторяемость и разнообразие", Day4TemperatureBenchmark.RunAsync),
        new("day5", "Сравнение моделей по точности, времени и стоимости", Day5ModelComparisonBenchmark.RunAsync),
        new("day6", "Первый агент и цепочки инструментов", FoundationBenchmarks.HelpAsync),
        new("day7", "Сохранение и восстановление контекста", FoundationBenchmarks.HelpAsync),
        new("day8", "Работа с токенами и переполнение", FoundationBenchmarks.HelpAsync),
        new("day9", "Сжатие истории: память и расход токенов", Day9CompressionBenchmark.RunAsync),
        new("day10", "Три стратегии контекста и независимые ветки", Day10ContextBenchmark.RunAsync),
        new("day11", "Три слоя памяти: состав, токены и пошаговая очистка", Day11MemoryLayersBenchmark.RunAsync),
        new("day12", "Персонализация: профили, пополнение и устойчивость", Day12PersonalizationBenchmark.RunAsync),
        new("day13", "Состояние задачи: автомат, паузы и контролируемые переходы", Day13TaskStateBenchmark.RunAsync),
        new("day14", "Инварианты: проверка решений кодом и 8 попыток обхода", Day14InvariantsBenchmark.RunAsync),
        new("day15", "Обновление памяти и разрешение конфликтов", Day15MemoryUpdatesBenchmark.RunAsync)
    ];

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            return await Day3PromptingBenchmark.RunAsync([]);
        }

        if (IsHelp(args[0]))
        {
            PrintHelp();
            return 0;
        }

        BenchmarkEntry? entry = Entries.FirstOrDefault(candidate =>
            candidate.Id.Equals(args[0], StringComparison.OrdinalIgnoreCase));
        if (entry is not null)
        {
            return await entry.Run(args[1..]);
        }

        // Preserve the original Day-3 invocation with flags but no explicit ID.
        if (args[0].StartsWith("--", StringComparison.Ordinal))
        {
            return await Day3PromptingBenchmark.RunAsync(args);
        }

        Console.Error.WriteLine($"Неизвестный бенч '{args[0]}'. Доступны: {string.Join(", ", Entries.Select(e => e.Id))}.");
        PrintHelp();
        return 2;
    }

    private static bool IsHelp(string value) =>
        value.Equals("help", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("-h", StringComparison.OrdinalIgnoreCase);

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Запуск бенчей:
              dotnet run -- --benchmark [day1|day2|day3|day4|day5|day6|day7|day8|day9|day10|day11|day12|day13|day14|day15] [параметры]

            Если день не указан, запускается Day-3.
            Для параметров конкретного теста используйте его --help:
              dotnet run -- --benchmark day3 --help
              dotnet run -- --benchmark day4 --help
              dotnet run -- --benchmark day5 --help
              dotnet run -- --benchmark day9 --help
              dotnet run -- --benchmark day10 --help
              dotnet run -- --benchmark day11 --help
              dotnet run -- --benchmark day12 --help
              dotnet run -- --benchmark day13 --help
              dotnet run -- --benchmark day14 --help
              dotnet run -- --benchmark day15 --help

            Доступные бенчи:
            """);

        foreach (BenchmarkEntry entry in Entries)
        {
            Console.WriteLine($"  {entry.Id,-5} {entry.Description}");
        }

        Console.WriteLine();
        Console.WriteLine("Новый бенч добавляется отдельным классом и одной записью в BenchmarkCommand.Entries.");
    }

    private sealed record BenchmarkEntry(
        string Id,
        string Description,
        Func<string[], Task<int>> Run);
}
