using UniversalAI.Benchmarks;

namespace UniversalAI.Benchmarks.Runtime;

/// <summary>User-facing lesson and input evidence, frozen as experiment-materials.json before execution.</summary>
public sealed record ExperimentMaterials
{
    public int SchemaVersion { get; init; } = 1;
    public string Title { get; init; } = "";
    public string Goal { get; init; } = "";
    public string? Assignment { get; init; }
    public string? AssignmentSource { get; init; }
    public List<string> Plan { get; init; } = [];
    public List<ExperimentMaterialGroup> Groups { get; init; } = [];
}
public sealed record ExperimentMaterialGroup(string Title, string Description, List<ExperimentMaterialItem> Items);
public sealed record ExperimentMaterialItem(string Label, string Text, string? Expected = null);

public static class BenchmarkMaterialsCatalog
{
    public static ExperimentMaterials Create(BenchmarkDefinition definition)
    {
        var materials = new ExperimentMaterials { Title = definition.Name, Goal = definition.Description };
        if (definition.Id is "day3" or "day5")
        {
            materials = materials with { Plan = definition.Id == "day5"
                ? ["Один набор задач передаётся каждой сравниваемой модели.", "Ответы проверяются по сохранённым эталонам.", "Сравниваются точность, usage, время и расчётная стоимость."]
                : ["Одни задачи решаются разными стратегиями промптинга и уровнями reasoning.", "Сохраняются исходные ответы и результаты оценивания."] };
            materials.Groups.Add(new("Проверочные задачи", "Один и тот же набор для сравниваемых вариантов.",
                Day3TaskSet.Tasks.Select(t => new ExperimentMaterialItem(t.Id, t.Question, string.Join(" / ", t.ExpectedAnswers))).ToList()));
        }
        if (definition.Id == "day4")
            materials = materials with { Plan = ["Сравнить ответы при разных значениях temperature.", "Повторить запросы и оценить правильность, формат и разнообразие."] };
        if (definition.Id is "day9" or "day10")
            materials = materials with { Plan = ["Подготовить общий диалог и сохранить исходные материалы.", "Проверить сохранение фактов при разных способах работы с контекстом.", "Сопоставить ответы с эталонами и измерениями расхода API."] };
        if (definition.Id == "day11")
        {
            materials = materials with
            {
                AssignmentSource = "Задание пользователя · DAYS.md, День 11 · копия от 22.11.2026",
                Assignment = """
                    Опишите и реализуйте модель памяти для ассистента.

                    Разделите информацию минимум на 3 типа:
                    - краткосрочная (текущий диалог)
                    - рабочая (данные текущей задачи)
                    - долговременная (профиль, решения, знания)

                    Сделайте так, чтобы:
                    - разные типы памяти хранились отдельно
                    - вы явно выбирали, что и куда сохраняется
                    - facts из Дня 10 стали основой долговременной памяти
                    - для каждого слоя было правило, что именно из него попадает в промпт

                    Проверьте:
                    - какие данные попадают в каждый слой
                    - сколько токенов добавляет каждый слой к запросу
                    - очистите слои по очереди и зафиксируйте, на какие вопросы агент перестаёт отвечать

                    Результат: агент с явной моделью памяти (memory layers).
                    """,
                Plan = ["Заполнить диалог, рабочую память и долговременные facts.", "Задать шесть независимых контрольных вопросов.", "Очистить диалог, затем рабочую память, затем facts; после каждого шага повторить вопросы.", "Ответы проверок не записывать обратно в память."]
            };
            materials.Groups.Add(new("Текущий диалог", "Исходные реплики до очистки.", Day11MemoryLayersBenchmark.InitialHistory().Select(m => new ExperimentMaterialItem(m.Role, m.Content)).ToList()));
            materials.Groups.Add(new("Рабочая память", "Данные текущей задачи.", Day11MemoryLayersBenchmark.InitialWorking().Select(p => new ExperimentMaterialItem(p.Key, p.Value)).ToList()));
            materials.Groups.Add(new("Долговременные facts", "Явно сохранённые знания.", Day11MemoryLayersBenchmark.InitialLongTerm().Select(p => new ExperimentMaterialItem(p.Key, p.Value)).ToList()));
            materials.Groups.Add(new("Контрольные вопросы", "После удаления источника ожидается НЕИЗВЕСТНО.", Day11MemoryLayersBenchmark.Probes.Select(p => new ExperimentMaterialItem(p.Layer, p.Question, p.Value)).ToList()));
        }
        if (definition.Id == "day12")
        {
            materials = materials with
            {
                AssignmentSource = "Задание пользователя · DAYS.md, День 12 · копия от 22.11.2026",
                Assignment = """
                    День 12. Персонализация ассистента

                    Добавьте персонализацию поверх модели памяти:
                    - создайте профиль пользователя
                    - опишите предпочтения (стиль, формат, ограничения)
                    - подключите профиль к каждому запросу
                    - сделайте так, чтобы профиль пополнялся из диалога, а не только задавался вручную

                    Проверьте:
                    - заведите 2–3 профиля с противоположными предпочтениями (кратко / подробно, с кодом / без кода, формально / неформально)
                    - задайте всем один и тот же вопрос
                    - посчитайте, в скольких ответах предпочтение реально соблюдено

                    Проверьте устойчивость:
                    - задайте 10 вопросов подряд в одном профиле
                    - зафиксируйте, на каком по счёту ответе ассистент начинает игнорировать предпочтения

                    Результат: Персонализированный агент, адаптированный под пользователя
                    """,
                Plan = ["Задать два противоположных профиля вручную.", "Пополнить третий профиль из явной реплики пользователя.", "Задать трём профилям один вопрос независимо.", "Задать обученному профилю 10 вопросов подряд с общей историей.", "Посчитать соблюдение трёх предпочтений и первый номер нарушения."]
            };
            materials.Groups.Add(new("Профили", "Начальные данные сценария; третий профиль обучается из реплики.",
                Day12PersonalizationBenchmark.Profiles().Select(p => new ExperimentMaterialItem(p.Name, Day12PersonalizationBenchmark.DescribeProfile(p))).ToList()));
            materials.Groups.Add(new("Пополнение из диалога", "Эталон задан независимо от результата извлечения модели.",
                [new("Реплика", Day12PersonalizationBenchmark.LearningText, Day12PersonalizationBenchmark.DescribeProfile(Day12PersonalizationBenchmark.LearnedExpectation()))]));
            materials.Groups.Add(new("Вопросы", "Один общий вопрос и десять последовательных.",
                new[] { Day12PersonalizationBenchmark.Question }.Concat(Day12PersonalizationBenchmark.StabilityQuestions).Select((q, i) => new ExperimentMaterialItem(i == 0 ? "Общий вопрос" : $"Устойчивость {i}", q)).ToList()));
        }
        if (definition.Id == "day13")
        {
            materials = materials with
            {
                AssignmentSource = "Задание пользователя · DAYS.md, День 13 · копия от 22.11.2026",
                Assignment = """
                    День 13. Состояние задачи (Task State Machine)

                    Реализуйте состояние задачи как конечный автомат:
                    - этап задачи
                    - текущий шаг
                    - ожидаемое действие

                    Пример состояний: planning → execution → validation → done

                    Сделайте так, чтобы:
                    - у задачи были допустимые состояния
                    - были разрешённые переходы между ними
                    - ассистент не мог «перепрыгнуть» этап
                    - переход проверялся кодом до применения, а не только описывался в промпте

                    Пример:
                    - нельзя делать реализацию до утверждённого плана
                    - нельзя делать финал без валидации

                    Проверьте:
                    - паузу на любом этапе
                    - продолжение без повторных объяснений
                    - попытки перейти в недопустимое состояние
                    - реакцию ассистента на запрещённый переход
                    - попросите ассистента напрямую нарушить порядок этапов и убедитесь, что переход не произошёл

                    Результат: Агент с формализованным состоянием задачи и контролируемым жизненным циклом
                    """,
                Plan = ["Создать задачу и попытаться пропустить утверждение/этапы.", "Подготовить и явно утвердить план.", "Выполнить реализацию, затем валидацию и финал.", "Проверить паузу и восстановление сохранённого шага во всех четырёх состояниях.", "Сохранить каждый отказ, предложение модели и решение кода отдельно."]
            };
            materials.Groups.Add(new("Цель задачи", "Исходная задача эксперимента; внешний код не запускается.", [new("Задача", Day13TaskStateBenchmark.Goal)]));
            materials.Groups.Add(new("Шаги и эталоны", "Разрешение/отказ, этап и пауза задаются сценарием независимо от модели.",
                Day13TaskStateBenchmark.Probes.Select((p, i) => new ExperimentMaterialItem($"{i + 1}. {p.Group}", p.Input,
                    $"Разрешено={p.Allowed}; этап={p.Stage}; пауза={p.Paused}; намерение={p.RequestedAction}")).ToList()));
        }
        return materials;
    }
}
