# UniversalAI

Учебная лаборатория на .NET 10: чат с агентами, облачные и локальные LLM, эксперименты с промптами, моделями и памятью. Консоль и веб используют одно ядро.

## Быстрый запуск

Нужен .NET SDK 10.0. Конфигурация провайдеров находится в `appsettings.json`. API-ключи задаются переменными окружения, в конфигурации хранятся только их имена.

```powershell
dotnet build UniversalAI.sln

# Автономная консоль
dotnet run --project UniversalAI.csproj

# Локальный веб-сервер: http://localhost:5080
dotnet run --project src/UniversalAI.Web

# Консоль как клиент уже работающего сервера
dotnet run --project UniversalAI.csproj -- --server http://localhost:5080
```

Один процесс владеет каталогом данных. Для одновременной работы веба и консоли запускайте консоль с `--server`. Автономной консоли можно выделить отдельный `UNIVERSALAI_DATA_DIR`.

## Где лежат данные

По умолчанию — `LocalApplicationData/UniversalAI`: в Windows это обычно `%LOCALAPPDATA%\UniversalAI`, в Linux — каталог данных текущего пользователя. Путь не зависит от рабочего каталога и репозитория.

```powershell
$env:UNIVERSALAI_DATA_DIR = 'D:\UniversalAI-Data'
dotnet run --project src/UniversalAI.Web
```

```text
UniversalAI-Data/
  universalai.db             диалоги, сообщения, summary и facts
  keys/                     ключи cookie/antiforgery веб-сервера
  runs/<run-id>/
    run.json                настройки, состояние и версии методики
    pricing.json            снимок тарифов
    output.log              журнал выполнения
    artifacts/              JSON, JSONL, CSV, Markdown и checkpoints
```

Новые прогоны никогда не перезаписывают предыдущие. Существующий `results/` в репозитории оставлен как исторический материал; рабочее приложение туда по умолчанию не пишет. Зафиксированный входной диалог Day-10 отдельно хранится в `src/UniversalAI.Benchmarks/Scenarios/`: это набор входных данных, а не выходной каталог.

**Учебная БД пересоздаётся при изменении её схемы.** Инициализатор сравнивает отпечаток схемы EF Core. Это сознательная политика проекта; сохранённые файлы прогонов не удаляются. Миграции отложены: [ROADMAP.md](ROADMAP.md).

## Облачные и локальные модели

```powershell
$env:PROXYAPI_API_KEY = '...'
$env:GIGACHAT_AUTH_KEY = '...'
```

В консоли:

```text
/provider                 список профилей
/provider proxyapi
/provider ollama
/models                   модели выбранного endpoint
/model qwen3:8b
/agent direct
/memory Summary
/memory SlidingWindow
/memory StickyFacts
/branch Альтернативный вариант
```

Подготовлены профили Ollama (`http://localhost:11434/v1`) и LM Studio (`http://localhost:1234/v1`). Сам сервер модели нужно запустить отдельно; для LM Studio следует указать ID реально загруженной модели. Возможности параметров зависят от модели и сервера: задавайте их в профиле. Инструменты локальных профилей изначально отключены; включайте `SupportsTools`, когда конкретная модель и сервер поддерживают их протокол.

`localhost` всегда обозначает компьютер **с сервером UniversalAI**. После переноса на VPS локальная LLM на домашнем ПК потребует доступного серверу адреса или туннеля.

Настройки можно переопределять через игнорируемый Git файл `appsettings.local.json` в корне проекта либо переменные `UNIVERSALAI_Providers__...`. Файл копируется при сборке; после его создания пересоберите проект. Массивы конфигурации .NET объединяются по индексам: пример переопределения готового профиля — `{"Providers":{"Profiles":{"2":{"DefaultModel":"your-model"}}}}`.

## Эксперименты

```powershell
dotnet run --project UniversalAI.csproj -- --benchmark --help
dotnet run --project UniversalAI.csproj -- --benchmark day9 --auto
dotnet run --project UniversalAI.csproj -- --benchmark day4 --provider ollama --model qwen3:8b
dotnet run --project UniversalAI.csproj -- --runs

# Серверный запуск: процесс продолжит работу после выхода клиента
dotnet run --project UniversalAI.csproj -- --server http://localhost:5080 --benchmark day9 --auto
dotnet run --project UniversalAI.csproj -- --server http://localhost:5080 --runs
dotnet run --project UniversalAI.csproj -- --server http://localhost:5080 --cancel RUN_ID
```

Day-3 требует поддержку `reasoning_effort`; Day-5 сохраняет методику сравнения трёх зафиксированных моделей ProxyAPI. Для произвольной локальной модели подходят другие эксперименты с совместимыми возможностями. Старые псевдонимы `--benchmark-dayN` и `--output` автономной консоли сохранены; `--output` экспортирует копию результатов, основной каталог запуска остаётся отдельным.

Во веб-интерфейсе доступны история прогонов, прогресс, отмена, артефакты и сравнение двух запусков. Сравнение общих метрик учитывает весь прогон; отчёты Day-9/10 дополнительно разделяют подготовку, сравнение и ветки. Запуск с ошибкой или отменой не становится завершённым измерением. После перезапуска оставшиеся Queued/Running помечаются Interrupted; платные вызовы автоматически не повторяются.

## Доступ с другого устройства

Без токена разрешены только loopback-подключения. Для доступа по сети задайте токен и адрес прослушивания:

```powershell
$env:UNIVERSALAI_SERVER_TOKEN = 'ваш-длинный-случайный-токен'
dotnet run --project src/UniversalAI.Web -- --urls http://0.0.0.0:5080
```

Браузер использует форму входа и cookie. Для удалённой консоли задайте ту же переменную токена на клиенте. API принимает `Authorization: Bearer ...`. Это одно личное рабочее пространство, а не система отдельных учётных записей.

На VPS разместите ASP.NET Core за HTTPS reverse proxy, задайте постоянный каталог данных и сохраняйте каталог `keys/` между перезапусками. При завершении локального тестирования порт можно оставить привязанным к `localhost`. Готовые команды публикации и границы расширения: [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

## Проверка без платных запросов

```powershell
dotnet build UniversalAI.sln
dotnet run --project tests/UniversalAI.Checks
dotnet run --project UniversalAI.csproj -- --benchmark day9 --self-test
dotnet run --project UniversalAI.csproj -- --benchmark day10 --self-test
```

Проверки приложения используют SQLite и подставные провайдеры: конкурентные ходы, ветки, валидация, память, все пять типизированных бенчмарков, отмена, изоляция вывода и восстановление состояния очереди. Они проверяют реализацию, а не качество реальной модели. Их артефакты находятся в игнорируемом `.local/checks/`.

Архитектура: [ARCHITECTURE.md](ARCHITECTURE.md). Методики экспериментов: [BENCHMARK.md](BENCHMARK.md).
