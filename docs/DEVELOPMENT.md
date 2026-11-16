# Разработка и расширение

## Новое задание или эксперимент

Добавляйте отдельный класс в `src/UniversalAI.Benchmarks`, набор данных и оценивание держите отдельно от исполнения. Реализуйте `IBenchmark` и зарегистрируйте его в `BenchmarkRegistration.AddUniversalAIBenchmarks`:

```csharp
public sealed class MyExperiment : IBenchmark
{
    public BenchmarkDefinition Definition => new(
        "lesson11", "Название задания", "Что сравнивает эксперимент", HasModel: true,
        ScenarioVersion: "1", ScorerVersion: "1");

    public async Task<int> ExecuteAsync(BenchmarkSettings settings, BenchmarkExecutionContext context)
    {
        var provider = context.CreateProvider(); // автоматически записывает запросы и usage
        var response = await provider.SendMessageAsync(new ChatRequest
        {
            Model = settings.Model!,
            Messages = [new() { Role = "user", Content = "Входная задача" }]
        }, context.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(context.Directory, "answer.txt"),
            response.Content, context.CancellationToken);
        context.Output.WriteLine("Готово");
        return 0;
    }
}

// Регистрация; интерфейс автоматически появится в веб-каталоге и API.
services.AddSingleton<IBenchmark, MyExperiment>();
```

Имя эксперимента не обязано начинаться с `day`. Обычный CLI-запуск использует тот же реестр: `--benchmark lesson11`. Для особых параметров расширяйте типизированные настройки и их проверку либо вводите собственный тип конфигурации модуля. Не передавайте shell-команды и пути запуска процессов из веб-форм.

При изменении сценария или оценивания увеличивайте соответствующую версию в Definition. Порядок вызовов, повторы, независимость контрольных вопросов и учёт подготовительных вызовов — часть методики. Для неизвестного тарифа оставляйте стоимость Н/д. Подставные ответы используйте только в проверках, отдельно от измерений реальной модели.

## Новый провайдер

Для другого OpenAI-compatible endpoint добавьте профиль в `appsettings.json`/`appsettings.local.json`:

```json
{
  "Id": "my-local",
  "Name": "Моя локальная LLM",
  "Type": "OpenAICompatible",
  "BaseUrl": "http://localhost:8000/v1",
  "DefaultModel": "loaded-model-id",
  "RequireApiKey": false,
  "SupportsTools": false,
  "ModelCapabilities": {
    "SupportsTemperature": true,
    "SupportsTopP": true,
    "SupportsFrequencyPenalty": false,
    "SupportsPresencePenalty": false,
    "SupportsSeed": false,
    "SupportsReasoningEffort": false
  }
}
```

Это формат одного элемента `Providers:Profiles`; добавьте его в массив с уникальным ID. Возможности задаются для выбранной модели/сервера, а не объявляются универсальными свойствами Ollama или LM Studio. Для модели с другим набором возможностей создайте отдельный профиль. Если ключ нужен, укажите `ApiKeyEnvironmentVariable` и `RequireApiKey: true`.

Если сервер использует другой протокол, реализуйте `IAIProvider`, подключите его создание в `IProviderFactory` и проверьте совместимость сообщений/tool calls. Добавляйте новый клиент через `IHttpClientFactory`. Валидация общих настроек принадлежит службам приложения, а преобразование API-параметров — адаптеру провайдера.

## Новый агент и инструменты

Реализуйте `IAgent`, зарегистрируйте `services.AddScoped<IAgent, MyAgent>()`. `AgentRegistry` автоматически подхватит его для консоли, API и веба. Передавайте CancellationToken всем внешним вызовам. Сохраняйте целый завершённый ход, включая сообщения инструментов. Не держите транзакцию SQLite во время ожидания модели.

Новые инструменты добавляются в `AgentToolRegistry`. До появления инструментов с побочными эффектами отдельно определите их права, отмену и повторное выполнение. Интерфейсы не должны обходить `WorkspaceService` для изменения диалога.

## Конфигурация и тарифы

- Общая конфигурация: корневой `appsettings.json`, затем необязательный `appsettings.local.json`, затем `UNIVERSALAI_...`.
- Каталог данных: `UNIVERSALAI_DATA_DIR`; выбирайте постоянный абсолютный путь.
- Тарифы: `src/UniversalAI.Engine/Configuration/pricing.json`, либо `UNIVERSALAI_PRICING_FILE` — путь к своему JSON. Меняйте Snapshot/Source вместе с ценами.
- `UNIVERSALAI_SOURCE_REVISION` — идентификатор коммита/сборки для метаданных прогонов. В CI задавайте его при запуске опубликованного приложения.
- Дополнительные секреты не должны попадать в DTO, журналы и `run.json`.

## API

Все API-вызовы находятся под `/api`. При заданном серверном токене нужен заголовок `Authorization: Bearer ...`.

| Метод | Путь | Операция |
|---|---|---|
| GET | `/health`, `/providers`, `/agents` | Состояние и каталоги |
| GET/POST | `/conversations` | Список / создание диалога |
| GET | `/conversations/{id}` | Настройки и память |
| PUT | `/conversations/{id}/settings` | Изменение настроек |
| GET | `/conversations/{id}/messages?offset=0&limit=100` | Страница истории |
| POST | `/conversations/{id}/messages` | Ход: `{"content":"..."}` |
| POST | `/conversations/{id}/branch` | Ветка: `{"title":"..."}` |
| POST | `/conversations/{id}/reset`, `/compress` | Очистка / принудительное summary |
| GET | `/conversations/{id}/models`, `/tokens` | Модели / статистика |
| GET | `/benchmarks` | Каталог экспериментов |
| GET/POST | `/runs` | Список / постановка эксперимента |
| GET | `/runs/{id}`, `/runs/{id}/metrics`, `/runs/{id}/log` | Состояние, метрики, хвост журнала |
| POST | `/runs/{id}/cancel` | Отмена |
| GET | `/runs/{id}/artifacts`, `/runs/{id}/artifacts/{name}` | Список / скачивание файлов |

Тело POST `/api/runs`:

```json
{"benchmarkId":"day9","settings":{"providerId":"proxyapi","model":"openai/gpt-4o-mini"}}
```

Ответ — 202 и RunId. Для чтения истории `limit` ограничен 500. Серверные эксперименты используют заданные администратором профили; произвольный путь `TranscriptPath` через API не принимается. В браузере скачивание использует `/downloads/{runId}/{name}` с cookie-сессией.

## Сборка для VPS

```powershell
dotnet publish src/UniversalAI.Web -c Release -o .local/publish/web
dotnet publish UniversalAI.csproj -c Release -o .local/publish/cli
```

Публикация переносится вместе с `appsettings.json`, `pricing.json`, `Scenarios/` и веб-ресурсами. На Linux нужен .NET 10 runtime/ASP.NET Core runtime соответствующей архитектуры либо отдельная self-contained публикация. Назначьте постоянный `UNIVERSALAI_DATA_DIR`, задайте токен и ключи провайдеров в окружении службы. Reverse proxy должен передавать WebSocket-соединение для Blazor и обслуживать HTTPS. Первый вариант размещения — один экземпляр приложения.

При обновлении учебная SQLite может быть пересоздана; файлы `runs/` остаются. Для сохранения диалогов между будущими изменениями схемы сначала реализуйте отложенные миграции. Потеря ключей `keys/` завершит старые браузерные сессии.

## Проверки

Проверка JavaScript Blazor, CSS и установления соединения (в том числе для локального Production-запуска):

```powershell
dotnet build src/UniversalAI.Web -c Release
./tests/Verify-WebAssets.ps1
./tests/Verify-WebAssets.ps1 -Environment Development
```

Скрипт запускает собственный сервер на отдельном порту с изолированными данными и завершает его после проверки. Для проверки опубликованной сборки передайте `-AssemblyPath .local/publish/web/UniversalAI.Web.dll` после `dotnet publish`.

`UniversalAI.Checks` — исполняемые интеграционные проверки без внешнего тестового фреймворка и без платных запросов. Основная команда: `dotnet run --project tests/UniversalAI.Checks`.

Для ручной проверки через HTTP можно поднять подставную LLM:

```powershell
dotnet run --project tests/UniversalAI.Checks -- --serve-fake --urls http://localhost:5091
```

В другом процессе переопределите Ollama endpoint переменной `UNIVERSALAI_Providers__Profiles__2__BaseUrl=http://localhost:5091/v1` и запустите веб. Ответы этого сервера тестовые; они не измеряют качество модели.
