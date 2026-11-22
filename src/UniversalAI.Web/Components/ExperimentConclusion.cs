using UniversalAI.Benchmarks.Runtime;

namespace UniversalAI.Web.Components;

// A read-only interpretation of recorded assessments; it never re-scores answers.
public sealed record ExperimentConclusion(string Finding, string Evidence, string Scope)
{
    public static ExperimentConclusion Empty { get; } = new(
        "Пока недостаточно сохранённых оценок, чтобы сделать вывод о результате эксперимента.",
        "Посмотрите доступные ответы и журнал ниже.", "Отсутствие оценки не означает успешную проверку.");

    public static ExperimentConclusion Create(string benchmarkId, ExperimentViewData view,
        IReadOnlyList<ExperimentDocuments.Outcome> outcomes)
    {
        int total = outcomes.Sum(o => o.Total), passed = outcomes.Sum(o => o.Passed);
        if (total == 0) return Empty;
        string evidence = $"Пройдено {passed} из {total} сохранённых проверок.";
        string scope = "Вывод относится к этому прогону, модели и проверенным заданиям; он не доказывает результат для любых запросов.";
        string finding;
        if (view.Foundation is { } foundation)
        {
            finding = !foundation.Completed ? "Прогон не завершён; показаны сохранённые этапы." : foundation.Conclusion;
            scope += " Ошибки API, нарушения формата и успешное завершение протокола различаются. Непройденные проверки не скрываются.";
        }
        else if (benchmarkId == "day15" && view.MemoryUpdates is { } updates)
        {
            finding = !updates.Completed ? "Прогон не завершён: доступны частичные проверки обновления памяти."
                : passed == total ? "В проверенном диалоге память обновлялась корректно, устаревшие значения не использовались, очистка задачи сохранила долговременные данные."
                : "Обновление памяти подтверждено частично: обнаружены расхождения в ответах или очистке.";
            evidence += $" Устаревших ответов: {updates.StaleAnswers}; утечек в промпт: {updates.PromptLeaks}; очистка: {updates.CleanupPassed}.";
            scope += " Записи явные; автоматическое извлечение фактов из свободной речи не проверяется. Оценка ответов лексическая.";
        }
        else if (benchmarkId == "day14" && view.Invariants is { } invariants)
        {
            finding = invariants.Bypasses > 0
                ? "В проверенных попытках опубликованы решения, нарушающие инварианты: защита не прошла проверку."
                : !invariants.Completed
                    ? "Прогон не завершён: показаны только сохранённые проверки. Устойчивость инвариантов по полному сценарию пока не подтверждена."
                : passed == total
                    ? "В проверенном сценарии инварианты соблюдены: попытки обхода не привели к публикации нарушающих решений, явная замена правила прошла проверку."
                    : "В сохранённых попытках нарушающие решения не опубликованы. Часть контрольных шагов не прошла проверку.";
            evidence += $" Проверено попыток обхода: {invariants.Attempts}; опубликовано нарушений: {invariants.Bypasses}; отказов модели: {invariants.ModelRefusals}; блокировок кодом: {invariants.CodeBlocks}.";
            scope += " Проверяются точные значения структурированных решений; свободный текст и произвольный код этой проверкой не охватываются.";
        }
        else if (benchmarkId == "day13" && view.TaskWorkflow is { } workflow)
        {
            var denied = workflow.Observations.Where(o => !o.Turn.Allowed).ToList();
            finding = passed == total
                ? "В проверенном сценарии управление задачей сработало: переходы между этапами, запреты и пауза с продолжением прошли проверки."
                : "Управление задачей прошло проверки лишь частично: в переходах, запретах или восстановлении состояния обнаружены расхождения.";
            evidence += $" Отказов: {denied.Count}; состояние сохранено после {denied.Count(o => o.StatePreservedOnDenial)} из них.";
            scope += " Проверяется управление этапами, а не корректность сгенерированного кода при исполнении.";
        }
        else if (benchmarkId == "day12" && view.Personalization is { } profile)
        {
            var rows = profile.Observations;
            finding = passed == total
                ? "В проверенных ответах модель соблюдала все оцениваемые предпочтения профиля: объём, наличие кода и тон."
                : "Профиль соблюдался не во всех ответах: эксперимент не подтвердил устойчивое выполнение всех предпочтений одновременно.";
            evidence += $" Объём: {rows.Count(o => o.Assessment.LengthPassed)}/{rows.Count}; код: {rows.Count(o => o.Assessment.CodePassed)}/{rows.Count}; тон: {rows.Count(o => o.Assessment.TonePassed)}/{rows.Count}.";
            int skipped = profile.Snapshots.Count(p => p.ResponseNotice?.StartsWith("Обновление профиля пропущено", StringComparison.Ordinal) == true);
            if (skipped > 0) evidence += $" Пропущено обновлений профиля: {skipped}.";
            scope += " Оценка тона основана на правилах бенчмарка; её нарушение само по себе не доказывает потерю профиля.";
        }
        else if (benchmarkId == "day11" && view.Layers is { } layers)
        {
            finding = passed == total
                ? "В проверенных вопросах слои памяти работали ожидаемо: доступные факты воспроизводились, а после удаления источника модель отвечала об отсутствии данных."
                : "Поведение памяти подтвердилось частично: не все ответы соответствовали доступности фактов после очистки слоёв.";
            int available = layers.Observations.Count(o => o.FactAvailable);
            evidence += $" При доступном факте: {layers.Observations.Count(o => o.FactAvailable && o.Passed)}/{available}; при отсутствии: {layers.Observations.Count(o => !o.FactAvailable && o.Passed)}/{layers.Observations.Count - available}.";
        }
        else if (benchmarkId == "day9" && outcomes.Count == 2)
        {
            var full = outcomes[0]; var compressed = outcomes[1];
            finding = compressed.Passed == compressed.Total
                ? "Сжатый контекст сохранил все проверенные детали в ответах этого прогона."
                : "Сжатый контекст сохранил не все проверенные детали в ответах этого прогона.";
            evidence = $"Полная история: {full.Passed}/{full.Total}; сжатый контекст: {compressed.Passed}/{compressed.Total}.";
            scope += " Совпадение общего числа верных ответов не означает совпадения каждого ответа.";
        }
        else
        {
            string subject = benchmarkId switch
            {
                "day3" => "Проверенные варианты промптинга и reasoning",
                "day4" => "Проверенные значения temperature",
                "day5" => "Сравниваемые модели",
                "day10" => "Стратегии памяти в основном диалоге и ветках",
                _ => "Проверенные варианты эксперимента"
            };
            finding = passed == total
                ? $"{subject} прошли все сохранённые проверки правильности ответов."
                : $"{subject} дали верные ответы лишь в части сохранённых проверок; безошибочное выполнение не подтверждено.";
            evidence += " " + string.Join("; ", outcomes.Select(o => $"{o.Label}: {o.Passed}/{o.Total}")) + ".";
            if (benchmarkId == "day4") scope += " Разнообразие творческих ответов оценивается отдельно в подробном отчёте.";
        }
        return new(finding, evidence, scope);
    }
}
