namespace UniversalAI.Benchmarks;

public sealed record BenchmarkTask(
    string Id,
    string Question,
    IReadOnlyList<string> ExpectedAnswers);

public static class Day3TaskSet
{
    // Shared by the prompting benchmark in Day-3 and model comparison in Day-5.
    public static IReadOnlyList<BenchmarkTask> Tasks { get; } =
    [
        new("arithmetic", "Вычислите: (48 / 6) * (7 + 2).", ["72"]),
        new("sum", "Чему равна сумма всех целых чисел от 1 до 50 включительно?", ["1275"]),
        new("sequence", "Продолжите последовательность: 3, 8, 15, 24, 35. Какое число следующее?", ["48"]),
        new("rectangle", "У прямоугольника длина 10 см, а периметр 34 см. Чему равна ширина в сантиметрах?", ["7"]),
        new("handshakes", "Четыре человека встретились и каждый один раз пожал руку каждому из остальных. Сколько всего было рукопожатий?", ["6"]),
        new("combinations", "Сколькими способами можно выбрать 2 разных объекта из 8, если порядок выбора не важен?", ["28"]),
        new("dice_probability", "Какова вероятность, что сумма значений двух честных шестигранных кубиков равна 7? Дайте точную дробь.", ["1/6", "0.1667", "16.67%"]),
        new("binary_strings", "Сколько двоичных строк длины 5 не содержат двух единиц подряд?", ["13"])
    ];
}
