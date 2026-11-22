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
        if (definition.Id is "day1" or "day2" or "day6" or "day7" or "day8")
        {
            materials = materials with { Plan = definition.Id switch {
                "day1" => ["Отправить запрос 2+2 через API.", "Сохранить и вывести исходный ответ, usage и проверку ответа 4."],
                "day2" => ["Один вопрос о трёх цветах светофора; 6 режимов × 3 повтора = 18 запросов.", "Без ограничений; явный JSON-формат; max_tokens=16; stop КОНЕЦ; JSON Schema strict=true; комбинация ограничений.", "Проверить парсинг и структуру каждого ответа кодом, сохранить ошибки/finish_reason и сравнить режимы."],
                "day6" => ["Отдельные диалоги для запроса без инструментов, calculate и зависимой цепочки roll_dice → calculate.", "Использовать production ToolCallingAgent и схемы трёх инструментов.", "Проверить число раундов и соответствие итогового произведения фактическому броску."],
                "day7" => ["Начать диалог, назвать код МАЯК-731; вычислить 17*19 через инструмент.", "Сохранить полную историю в SQLite, закрыть агент и DbContext.", "Создать runtime заново, сравнить восстановленные сообщения целиком и спросить код/результат без повторного инструмента."],
                _ => ["Сверить заявленное окно с выбранной моделью; для openai/gpt-4o-mini — 128000.", "Короткий диалог, пять длинных ходов: оценки текущего входа/истории и usage API.", "Отправить вход длиннее заявленного окна, сохранить фактическую ошибку переполнения. Успешный ответ не считать переполнением."] } };
        }
        if (definition.Id == "day15")
        {
            materials = materials with { Plan = ["Записать четыре исходных факта, задать контрольные вопросы.", "Заменить четыре значения, после каждой замены проверить ответ и промпт.", "Отклонить запоздавшую запись; накопить два решения списком.", "Завершить задачу, проверить очистку рабочих/временных данных и сохранность долговременных фактов. 13 API-запросов."] };
            materials.Groups.Add(new("Изменяемые факты", "Эталоны заданы сценарием; записи явные, с временем. История/audit не входят в модельный контекст.",
                Day15MemoryUpdatesBenchmark.Keys.Select((k, i) => new ExperimentMaterialItem(k,
                    Day15MemoryUpdatesBenchmark.Initial[i], Day15MemoryUpdatesBenchmark.Updated[i])).ToList()));
        }
        if (definition.Id == "day14")
        {
            materials = materials with
            {
                Plan = ["Задать четыре отдельных инварианта явными командами.", "Получить совместимый контрольный ответ.",
                    "Проверить 8 разных попыток обхода без изменения правил.", "Явно заменить C# на Rust и проверить новый ответ/журнал."]
            };
            materials.Groups.Add(new("Правила", "Фиксированные разрешённые значения решений. Произвольный текст модели не публикуется.",
                Day14InvariantsBenchmark.Commands.Select(c => new ExperimentMaterialItem("Команда", c)).ToList()));
            materials.Groups.Add(new("Попытки обхода", "Ожидается отказ или разрешённая альтернатива; состояние неизменно.",
                Day14InvariantsBenchmark.Attacks.Select((a, i) => new ExperimentMaterialItem($"{i + 1}", a)).ToList()));
        }
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
                Plan = ["Создать задачу и попытаться пропустить утверждение/этапы.", "Подготовить и явно утвердить план.", "Выполнить реализацию, затем валидацию и финал.", "Проверить паузу и восстановление сохранённого шага во всех четырёх состояниях.", "Сохранить каждый отказ, предложение модели и решение кода отдельно."]
            };
            materials.Groups.Add(new("Цель задачи", "Исходная задача эксперимента; внешний код не запускается.", [new("Задача", Day13TaskStateBenchmark.Goal)]));
            materials.Groups.Add(new("Шаги и эталоны", "Разрешение/отказ, этап и пауза задаются сценарием независимо от модели.",
                Day13TaskStateBenchmark.Probes.Select((p, i) => new ExperimentMaterialItem($"{i + 1}. {p.Group}", p.Input,
                    $"Разрешено={p.Allowed}; этап={p.Stage}; пауза={p.Paused}; намерение={p.RequestedAction}")).ToList()));
        }
        if (CourseAssignments.Find(definition.Id) is { } assignment)
            materials = materials with { Title = assignment.Title, Assignment = assignment.Text,
                AssignmentSource = $"{CourseAssignments.Snapshot.Source} · День {assignment.Day} · полный текст от {CourseAssignments.Snapshot.ReadOn} · SHA256 {assignment.Sha256}" };
        return materials;
    }
}
