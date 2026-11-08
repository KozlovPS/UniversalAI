# Архитектура UniversalAI

```text
Program.cs
  └─ Application/UniversalAIApplication.cs  — выбор режима и сборка зависимостей
       ├─ ConsoleApp/                        — ввод, цикл чата и обработчики slash-команд
       ├─ Services/ChatService.cs            — история, запрос к провайдеру, сохранение хода
       ├─ Services/ConversationService.cs    — операции с диалогами и сообщениями
       ├─ Services/Providers/                — выбор подключённого AI-провайдера
       ├─ Providers/                          — протоколы GigaChat и OpenAI-compatible
       ├─ Core/                               — модели запросов и проверка возможностей
       ├─ Data/                               — EF Core контекст и сущности SQLite
       └─ Benchmarks/                         — независимые эксперименты Day-3/4/5
```

## Границы ответственности

- `Program.cs` только запускает приложение.
- `UniversalAIApplication` является composition root: создаёт БД, провайдеры и службы, затем выбирает консольный или benchmark-режим.
- `ConsoleChatApplication` управляет интерактивным циклом и отображает ответы.
- `ConsoleCommandRouter` передаёт slash-команды специализированным обработчикам в `ConsoleApp/Commands`.
- `ChatService` готовит контекст из системного промпта и истории, вызывает текущий провайдер и сохраняет успешный ход. Его можно переиспользовать из другого интерфейса.
- `ConversationService` занимается только хранением и настройками диалогов.
- Каждый benchmark остаётся отдельным классом; `BenchmarkCommand` отвечает за выбор по ID.

Новая консольная команда добавляется отдельным `IConsoleCommandHandler` и регистрируется в `UniversalAIApplication`. Новый benchmark получает собственный класс с `RunAsync` и одну запись в `BenchmarkCommand`.
