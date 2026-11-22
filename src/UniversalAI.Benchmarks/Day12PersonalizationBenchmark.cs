using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UniversalAI.Benchmarks.Runtime;
using UniversalAI.Core;
using UniversalAI.Core.Capabilities;
using UniversalAI.Data.Entities;
using UniversalAI.Services;
using UniversalAI.Services.Agents;
using UniversalAI.Services.Context;

namespace UniversalAI.Benchmarks;

public static class Day12PersonalizationBenchmark
{
    public const string ScenarioVersion = "7";
    public const string ScorerVersion = "1";
    public const string Question = "Объясни, как работает кэширование результатов вычислений и когда оно полезно.";
    public const string LearningText = "Я предпочитаю краткие ответы, без кода. Общайся со мной неформально, на ты.";
    public static readonly string[] StabilityQuestions =
    [
        "Что такое очередь задач?", "Как работает словарь?", "Зачем нужны тесты?",
        "Что такое асинхронный метод?", "Как устроена пагинация?", "Для чего нужны интерфейсы?",
        "Что такое транзакция?", "Как работает отмена операции?", "Зачем нужен журнал событий?",
        "Как выбрать ключ кэша?"
    ];
    public static UserProfile[] Profiles() =>
    [
        new() { Name = "Кратко · без кода · формально", Length = "brief", Code = "forbidden", Tone = "formal", LearnFromDialogue = false },
        new() { Name = "Подробно · с кодом · неформально", Length = "detailed", Code = "required", Tone = "informal", LearnFromDialogue = false },
        new() { Name = "Обучаемый из диалога" }
    ];
    public static UserProfile LearnedExpectation() => new() { Name = "Обучаемый из диалога", Length = "brief", Code = "forbidden", Tone = "informal" };
    public static string DescribeProfile(UserProfile p) => $"Имя: {p.Name}\n" +
        "Объём: " + (p.Length == "brief" ? "кратко, до 80 слов" : p.Length == "detailed" ? "подробно, от 180 слов" : "не задан") +
        "\nКод: " + (p.Code == "required" ? "с примером кода" : p.Code == "forbidden" ? "без кода" : "по задаче") +
        "\nТон: " + (p.Tone == "formal" ? "формально, на Вы" : p.Tone == "informal" ? "неформально, на ты" : "не задан") +
        $"\nДополнительные предпочтения: {p.Constraints}\nПополнение из диалога: {(p.LearnFromDialogue ? "включено" : "выключено")}\n" +
        string.Join("\n", p.Evidence.Select(e => $"Источник {e.Key}: «{e.Value}»"));

    public sealed record Assessment(int Words, bool LengthPassed, bool CodePassed, bool TonePassed)
    { public bool Passed => LengthPassed && CodePassed && TonePassed; }
    public sealed record Observation(string Stage, string ProfileName, int Number, int CallNumber,
        string Question, string Answer, UserProfile Profile, UserProfile ExpectedProfile, Assessment Assessment, int ProfileTokens,
        int HistoryTokens, int? PromptTokens, int? CompletionTokens);
    public sealed record ProfileSnapshot(string Stage, UserProfile Before, UserProfile After,
        List<ChatMessage> PromptMessages, string? ResponseNotice = null);
    public sealed record Report(bool Completed, string? Error, string? Model, string ScenarioVersion,
        string ScorerVersion, List<Observation> Observations, List<ProfileSnapshot> Snapshots,
        int? FirstViolation, int StabilityAnswered);

    public static Assessment Score(UserProfile p, string answer)
    {
        int words = Regex.Matches(answer, @"[\p{L}\p{N}]+(?:[-'][\p{L}\p{N}]+)*").Count;
        bool code = answer.Contains("```") || answer.Contains('`') ||
            Regex.IsMatch(answer, @"(?m)^\s*(?:public |class |def |function |SELECT |var \w+\s*=)");
        bool formal = Regex.IsMatch(answer, @"\b(вы|вам|вас|ваш\w*)\b", RegexOptions.IgnoreCase);
        bool informal = Regex.IsMatch(answer, @"\b(ты|тебе|тебя|твой|твоя|твои|твоё)\b", RegexOptions.IgnoreCase);
        return new(words, p.Length == "unspecified" || (p.Length == "brief" ? words <= 80 : words >= 180),
            p.Code == "unspecified" || (p.Code == "required" ? answer.Contains("```") : !code),
            p.Tone == "unspecified" || (p.Tone == "formal" ? formal && !informal : informal && !formal));
    }

    public static async Task<int> ExecuteAsync(BenchmarkSettings settings, BenchmarkExecutionContext context)
    {
        var ct = context.CancellationToken;
        var provider = context.CreateProvider();
        var capabilities = context.Profile.ModelCapabilities ?? ModelCapabilityResolver.Resolve(context.Profile.Name, settings.Model!);
        var profiles = Profiles();
        List<Observation> observations = [];
        List<ProfileSnapshot> snapshots = [];
        var presentation = new ExperimentPresentation();
        int calls = 0;
        bool completed = false;
        string? error = null;
        Directory.CreateDirectory(context.Directory);
        void Log(string text) => context.Output.WriteLine(text);
        Log("ДЕНЬ 12 · Персонализация ассистента");
        Log($"Модель: {settings.Model}; провайдер: {context.Profile.Name}; окно истории: {settings.Keep} сообщений; лимит ответа/reasoning: не задан.");
        Log("План: три профиля → один общий вопрос → 10 вопросов подряд в обученном профиле.");
        Log("Всего 25 запросов API: 12 извлечений предпочтений и 13 ответов. 39 отдельных проверок предпочтений.");
        Log("Профили 1–2 задаются вручную; профиль 3 получает предпочтения из реплики, затем обновляется на каждом ходе.");
        Log("Объём: ≤80 / ≥180 слов; код: fenced-блок / отсутствие фрагментов; тон: Вы / ты. Это прозрачная эвристика, не оценка содержания.");
        Log($"Общий вопрос: {Question}\nРеплика для пополнения профиля: {LearningText}");
        try
        {
            var learning = new ExperimentStage { Id = "learn", Title = "Пополнение профиля из диалога", Description = LearningText };
            presentation.Stages.Add(learning);
            var before = profiles[2];
            learning.CallNumbers.Add(++calls);
            await SaveAsync();
            string? learningNotice = null;
            profiles[2] = await UserProfileMemory.LearnAsync(before, LearningText, settings.Model!, provider, ct,
                onResponseNotice: notice => learningNotice = notice);
            snapshots.Add(new("learn", before, profiles[2], [], learningNotice));
            if (learningNotice != null)
            {
                Log(learningNotice);
                learning.Changes.Add(new("Формат ответа извлечения", "Ожидался чистый JSON", learningNotice));
            }
            learning.Changes.Add(new("Профиль пользователя", DescribeProfile(before), DescribeProfile(profiles[2])));
            var target = LearnedExpectation();
            learning.Metrics.Add(new("Извлечение трёх предпочтений", profiles[2].Length == target.Length && profiles[2].Code == target.Code && profiles[2].Tone == target.Tone ? "3/3" : "Есть расхождения; см. снимок профиля"));
            Log("СОХРАНЕНО в профиль (не в рабочую память/facts): " + DescribeProfile(profiles[2]));
            await SaveAsync();
            for (int i = 0; i < profiles.Length; i++)
                await AnswerAsync($"compare-{i + 1}", $"Общий вопрос · профиль {i + 1}", profiles[i], [], Question, 1);
            List<ChatMessage> history = [];
            var stable = profiles[2];
            for (int i = 0; i < StabilityQuestions.Length; i++)
                stable = await AnswerAsync("stability", "Устойчивость · 10 вопросов подряд", stable, history, StabilityQuestions[i], i + 1);
            completed = true;
            var first = observations.FirstOrDefault(o => o.Stage == "stability" && !o.Assessment.Passed);
            Log($"ЗАВЕРШЕНО: {observations.Count(o => o.Assessment.Passed)}/13 ответов соблюдают все три предпочтения.");
            Log(first == null ? "Устойчивость: нарушений на 10 ответах не обнаружено; поведение после десятого не измерено." : $"Первое нарушение предпочтений на последовательном ответе №{first.Number}.");
        }
        catch (Exception e) { error = e.Message; throw; }
        finally { await SaveAsync(); }
        return 0;

        async Task<UserProfile> AnswerAsync(string stageId, string title, UserProfile profile, List<ChatMessage> history, string question, int number)
        {
            ct.ThrowIfCancellationRequested();
            var stage = presentation.Stages.FirstOrDefault(s => s.Id == stageId);
            if (stage == null) { stage = new() { Id = stageId, Title = title, Description = "Профиль включается заново в каждый запрос; ответы остаются в истории только последовательной серии." }; presentation.Stages.Add(stage); }
            var conversation = new ConversationEntity { Model = settings.Model!, MemoryStrategy = "Layered",
                ContextKeepLastMessages = settings.Keep, UserProfileJson = JsonSerializer.Serialize(profile) };
            if (profile.LearnFromDialogue) stage.CallNumbers.Add(++calls);
            await SaveAsync();
            var prepared = await new ContextCompressionService(new UnusedStore()).PrepareAsync(conversation, history, provider, ct, currentUserText: question);
            var effective = prepared.PendingProfile ?? profile;
            var request = new ChatRequest { Model = settings.Model!, Temperature = capabilities.SupportsTemperature ? 0 : null,
                Messages = [new() { Role = "system", Content = "Ты полезный ассистент. Отвечай на русском языке. Учитывай профиль пользователя." },
                    .. prepared.Messages, new() { Role = "user", Content = question }] };
            snapshots.Add(new(stageId, profile, effective, request.Messages.ToList(), prepared.ProfileResponseNotice));
            if (prepared.ProfileResponseNotice is { } notice)
            {
                Log(notice);
                stage.Changes.Add(new($"Формат извлечения перед ответом {number}", "Ожидался чистый JSON", notice));
            }
            stage.Changes.Add(new($"Профиль перед ответом {number}", DescribeProfile(profile), DescribeProfile(effective)));
            int call = ++calls; stage.CallNumbers.Add(call);
            Log($"\nЭТАП {stageId} · ответ {number} · {effective.Name}");
            Log($"КОНТЕКСТ: профиль ~{prepared.ProfileTokens} токенов, история ~{prepared.RawHistoryTokens}; в запросе {prepared.RecentMessages} сообщений истории. Оценки локальные, usage API отдельно.");
            foreach (var message in request.Messages) Log($"[{message.Role}] {message.Content}");
            var expected = profile.Name == "Обучаемый из диалога" ? LearnedExpectation() : profile;
            Log("ОЖИДАНИЕ (из сценария, независимо от результата извлечения): " + UserProfileMemory.AsMessage(expected).Content);
            await SaveAsync();
            var response = await provider.SendMessageAsync(request, ct);
            StickyFactsMemory.EnsureComplete(response);
            var assessment = Score(expected, response.Content);
            bool usage = response.PromptTokens > 0 || response.CompletionTokens > 0;
            observations.Add(new(stageId, profile.Name, number, call, question, response.Content, effective, expected,
                assessment, prepared.ProfileTokens, prepared.RawHistoryTokens,
                usage ? response.PromptTokens : null, usage ? response.CompletionTokens : null));
            history.Add(new() { Role = "user", Content = question });
            history.Add(new() { Role = "assistant", Content = response.Content });
            stage.Metrics.Clear();
            var rows = observations.Where(o => o.Stage == stageId).ToList();
            stage.Metrics.Add(new("Все предпочтения", $"{rows.Count(o => o.Assessment.Passed)}/{rows.Count}"));
            stage.Metrics.Add(new("Объём / код / тон", $"{rows.Count(o => o.Assessment.LengthPassed)} / {rows.Count(o => o.Assessment.CodePassed)} / {rows.Count(o => o.Assessment.TonePassed)}", $"Из {rows.Count} ответов"));
            Log("ОТВЕТ: " + response.Content);
            Log($"ОЦЕНКА: {(assessment.Passed ? "ПРОЙДЕНО" : "НАРУШЕНИЕ")} · слов {assessment.Words} · объём={assessment.LengthPassed}, код={assessment.CodePassed}, тон={assessment.TonePassed}");
            await SaveAsync();
            return effective;
        }

        async Task SaveAsync()
        {
            var stability = observations.Where(o => o.Stage == "stability").ToList();
            int? first = stability.FirstOrDefault(o => !o.Assessment.Passed)?.Number;
            await AtomicAsync("report.json", JsonSerializer.Serialize(new Report(completed, error, settings.Model, ScenarioVersion, ScorerVersion, observations, snapshots, first, stability.Count), FileRunStore.Json));
            await AtomicAsync("experiment-view.json", JsonSerializer.Serialize(presentation, FileRunStore.Json));
            var md = new StringBuilder("# День 12 — персонализация\n\n")
                .AppendLine($"Завершён: {completed}. Модель: {settings.Model}. Ответов: {observations.Count}/13. Ошибка: {error ?? "нет"}.")
                .AppendLine("\nТри профиля отвечают на один вопрос независимо. Затем обученный из диалога профиль отвечает на 10 вопросов подряд с общей историей. Профиль включается независимо от окна истории через production ContextCompressionService.")
                .AppendLine("\n| Серия | Объём | Код | Тон | Все предпочтения |\n|---|---:|---:|---:|---:|");
            foreach (var group in observations.GroupBy(o => o.Stage))
                md.AppendLine($"| {group.Key} | {group.Count(o => o.Assessment.LengthPassed)}/{group.Count()} | {group.Count(o => o.Assessment.CodePassed)}/{group.Count()} | {group.Count(o => o.Assessment.TonePassed)}/{group.Count()} | {group.Count(o => o.Assessment.Passed)}/{group.Count()} |");
            md.AppendLine($"\nУстойчивость: {stability.Count}/10 ответов. Первое нарушение: {(first?.ToString() ?? (stability.Count == 10 ? "не обнаружено в пределах 10 ответов" : "пока не обнаружено; серия неполная"))}.")
                .AppendLine("\nОценка v1: кратко ≤80 слов; подробно ≥180; с кодом — тройные кавычки Markdown; без кода — отсутствие обратных кавычек и распространённых строк программ. Формально — Вы/вам/вас/ваш без ты/тебе/тебя/твой; неформально — наоборот. Содержание и все возможные формы кода автоматически не проверяются. Общие предпочтения вне этих трёх осей требуют ручной оценки.")
                .AppendLine("Один сценарий/повтор, три профиля. Успешное завершение протокола не означает идеального соблюдения. API usage, время и тарифы — в api-calls.jsonl/pricing.json; локальные оценки профиля не являются точной токенизацией модели.");
            foreach (var snapshot in snapshots.Where(s => s.ResponseNotice != null))
                md.AppendLine($"\nПримечание формата ({snapshot.Stage}): {snapshot.ResponseNotice}");
            foreach (var row in observations)
                md.AppendLine($"\n## {row.Stage} · ответ {row.Number}\n\n{row.Question}\n\n{row.Answer}\n\nОценка: {row.Assessment.Passed}; слов {row.Assessment.Words}; объём {row.Assessment.LengthPassed}, код {row.Assessment.CodePassed}, тон {row.Assessment.TonePassed}. Профиль ~{row.ProfileTokens} токенов; API вход/выход {row.PromptTokens?.ToString() ?? "Н/д"}/{row.CompletionTokens?.ToString() ?? "Н/д"}.");
            await AtomicAsync("report.md", md.ToString());
        }
        async Task AtomicAsync(string name, string content)
        {
            string path = Path.Combine(context.Directory, name);
            await SnapshotFile.WriteAllTextAsync(path, content, CancellationToken.None);
        }
    }

    private sealed class UnusedStore : IContextMemoryStore
    {
        public Task<ContextSummaryEntity?> GetSummaryAsync(int id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SaveSummaryAsync(int id, string content, int count, ChatResult usage, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SaveFactsAsync(ConversationEntity c, Dictionary<string, string> facts, CancellationToken ct = default, ChatResult? usage = null) => throw new NotSupportedException();
    }
    public static Task<int> RunAsync(string[] args)
    {
        Console.WriteLine("День 12: 3 профиля, общий вопрос, 10 последовательных вопросов. 25 вызовов API, без программного лимита токенов.\nЗапуск: dotnet run -- --benchmark day12 --provider proxyapi --model openai/gpt-4o-mini --auto\nOffline: dotnet run --project tests/UniversalAI.Checks");
        return Task.FromResult(args.Any(a => a is "--help" or "-h" or "help") ? 0 : 2);
    }
}
