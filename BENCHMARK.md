# Бенчмарки UniversalAI

Все эксперименты запускаются через одну команду:

```powershell
dotnet run -- --benchmark day3
dotnet run -- --benchmark day4
dotnet run -- --benchmark day5
dotnet run -- --benchmark day9
```

Перед запуском задайте API-ключ. По умолчанию используется `PROXYAPI_API_KEY`:

```powershell
$env:PROXYAPI_API_KEY = "ваш API-ключ"
```

Справка по списку тестов: `dotnet run -- --benchmark --help`. Параметры конкретного теста: `dotnet run -- --benchmark dayN --help`.
Старые короткие команды `--benchmark-dayN` оставлены как совместимые псевдонимы; в новых инструкциях используется единый формат выше.

| Бенч | Содержание | Документация | Отчёты по умолчанию |
|---|---|---|---|
| Day-3 | Четыре приёма промптинга при low и максимальном reasoning | [Day-3](BENCHMARK-DAY-3.md) | `benchmark-results-Day-3.*` |
| Day-4 | Temperature 0, 0.7 и 1.2; два типа задач и пять повторов | [Day-4](BENCHMARK-DAY-4.md) | `benchmark-results-Day-4.*` |
| Day-5 | Бюджетная, средняя и флагманская модели на наборе задач Day-3 | [Day-5](BENCHMARK-DAY-5.md) | `benchmark-results-Day-5.*` |
| Day-9 | Сжатие истории: 30 сообщений, проверка деталей, сравнение токенов и вывод | [Day-9](BENCHMARK-DAY-9.md) | `results/day9/` |

Каждый прогон создаёт CSV, Markdown и JSON. Для дней 3–5 это файлы рядом с приложением; для дня 9 — один каталог `results/day9`. Параметр `--output ПУТЬ` задаёт файл CSV для дней 3–5 или каталог результатов для дня 9. День 9 по умолчанию показывает этапы с паузами по Enter; `--auto` отключает паузы.

## Добавление следующего бенча

1. Создайте отдельный класс и файл в `Benchmarks/`, названный по дню и задаче, например `Day6ContextBenchmark.cs`.
2. Реализуйте в классе статический метод `RunAsync(string[] args)`.
3. Добавьте одну запись с ID, описанием и методом запуска в `BenchmarkCommand.Entries`.
4. Добавьте `BENCHMARK-DAY-6.md` и строку в таблицу выше.

Общий вывод аргументов и выбор бенча находятся в `BenchmarkCommand`; общие проверки числовых ответов — в `BenchmarkAnswerScorer`. Для наборов задач используйте отдельные task-set классы, чтобы разные эксперименты могли повторно использовать неизменный датасет.
