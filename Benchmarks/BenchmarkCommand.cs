namespace UniversalAI.Benchmarks;

/// <summary>Single CLI entry point and registry for all benchmark runs.</summary>
public static class BenchmarkCommand
{
    private static readonly BenchmarkEntry[] Entries =
    [
        new("day3", "Приёмы промптинга и уровни рассуждения", Day3PromptingBenchmark.RunAsync),
        new("day4", "Влияние temperature на повторяемость и разнообразие", Day4TemperatureBenchmark.RunAsync),
        new("day5", "Сравнение моделей по точности, времени и стоимости", Day5ModelComparisonBenchmark.RunAsync)
    ];

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            return await Entries[0].Run([]);
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
            return await Entries[0].Run(args);
        }

        Console.Error.WriteLine($"Неизвестный бенч '{args[0]}'. Выберите day3, day4 или day5.");
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
              dotnet run -- --benchmark [day3|day4|day5] [параметры]

            Если день не указан, запускается Day-3.
            Для параметров конкретного теста используйте его --help:
              dotnet run -- --benchmark day3 --help
              dotnet run -- --benchmark day4 --help
              dotnet run -- --benchmark day5 --help

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
