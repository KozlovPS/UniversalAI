# Бенчмарки UniversalAI

Обычный запуск через CLI сохраняет отдельный прогон во внешнем каталоге данных. Веб-интерфейс и удалённая консоль используют тот же runner и методику.

```powershell
dotnet run -- --benchmark day3
dotnet run -- --benchmark day4
dotnet run -- --benchmark day5
dotnet run -- --benchmark day9 --auto
dotnet run -- --benchmark day10 --auto
```

По умолчанию облачным экспериментам нужен `PROXYAPI_API_KEY`. Профиль выбирается через `--provider`, модель — через `--model`. Day-3 требует reasoning; Day-5 сравнивает зафиксированные модели ProxyAPI. Пример локального запуска: `dotnet run -- --benchmark day4 --provider ollama --model qwen3:8b`.

| ID | Содержание | Методика |
|---|---|---|
| day3 | Четыре приёма промптинга при low и максимальном reasoning | [Day-3](BENCHMARK-DAY-3.md) |
| day4 | Temperature 0, 0.7 и 1.2; две задачи, пять повторов | [Day-4](BENCHMARK-DAY-4.md) |
| day5 | Сравнение трёх моделей по точности, времени и стоимости | [Day-5](BENCHMARK-DAY-5.md) |
| day9 | Summary против полной истории, сохранность 13 деталей | [Day-9](BENCHMARK-DAY-9.md) |
| day10 | Окно, facts, summary, checkpoints и независимые ветки | [Day-10](BENCHMARK-DAY-10.md) |

Справка: `dotnet run -- --benchmark --help`, параметры прежних протоколов: `--benchmark dayN --help`. Псевдонимы `--benchmark-dayN` сохранены. Параметры общего запуска и каталогов описаны в [README.md](README.md).

## Результаты

Корень задаётся `UNIVERSALAI_DATA_DIR`; по умолчанию — `LocalApplicationData/UniversalAI`. Каждый запуск создаёт `runs/<run-id>/`, внутри которого находятся метаданные, журнал, снимок тарифов и `artifacts/`. Для Day-3/4/5 отчёты называются `report.csv`, `report.json`, `report.md`; Day-9/10 сохраняют дополнительные сценарии, ответы и память. Все протоколы runner также записывают `api-requests.jsonl` и `api-calls.jsonl`.

`--output` автономной консоли экспортирует копию в указанный файл CSV (Day-3/4/5) либо каталог (Day-9/10). Источник остаётся в отдельной папке RunId. Для Day-10 каталог экспорта должен быть пустым. Старые методические документы показывают прежние имена выходных файлов; действующий каталог всегда печатается после запуска.

Отмена сохраняет частичные артефакты. После перезапуска незавершённые прогоны имеют состояние Interrupted, без автоматических повторов. Не считайте такой прогон полным сравнением стратегий.

## Офлайн-проверки и пересчёт

```powershell
dotnet run --project tests/UniversalAI.Checks
dotnet run -- --benchmark day9 --self-test
dotnet run -- --benchmark day10 --self-test
dotnet run -- --benchmark day9 --rescore ПУТЬ_К_ARTIFACTS
dotnet run -- --benchmark day10 --rescore ПУТЬ_К_ARTIFACTS
```

Self-test проверяет код на подставных провайдерах; rescore пересчитывает сохранённые ответы без сетевых запросов. Rescore изменяет отчёт по указанному пути; исходные ответы сохраняются. Исторический `results/` можно использовать для пересчёта, но новые прогоны туда автоматически не записываются.

## Следующие задания

Создавайте отдельные реализации `IBenchmark`, регистрируйте через DI и версионируйте сценарий/оценивание. Типизированный `ExecuteAsync` принимает настройки, провайдера, каталог, вывод и CancellationToken. Консоль и веб не должны содержать методику эксперимента. Пример реализации: [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).
