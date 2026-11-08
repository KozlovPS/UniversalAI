using Microsoft.EntityFrameworkCore;
using System.Globalization;
using UniversalAI.Core;
using UniversalAI.Data;
using UniversalAI.Data.Entities;
using UniversalAI.Providers;
using UniversalAI.Providers.GigaChat;
using UniversalAI.Providers.OpenAICompatible;
using UniversalAI.Services;
using UniversalAI.Services.Providers;

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Length > 0 && args[0].StartsWith("--benchmark", StringComparison.OrdinalIgnoreCase))
{
    string[] benchmarkArgs = args[0].Equals("--benchmark", StringComparison.OrdinalIgnoreCase)
        ? args[1..]
        : args[0].StartsWith("--benchmark-day", StringComparison.OrdinalIgnoreCase)
            ? new[] { args[0]["--benchmark-".Length..] }
            .Concat(args[1..])
            .ToArray()
            : args;

    int exitCode = await UniversalAI.Benchmarks.BenchmarkCommand.RunAsync(benchmarkArgs);
    Environment.ExitCode = exitCode;
    return;
}

Console.WriteLine("================================");
Console.WriteLine("       UniversalAI");
Console.WriteLine("================================");

// Настройки провайдеров
GigaChatOptions gigaOptions = GigaChatOptions.FromEnvironment();
OpenAICompatibleOptions proxyOptions = OpenAICompatibleOptions.FromEnvironment();

// Подключение SQLite
DbContextOptions<AppDbContext> dbOptions =
    new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlite("Data Source=universalai.db")
        .Options;

await using AppDbContext db = new(dbOptions);

await db.Database.EnsureCreatedAsync();

ConversationService conversationService = new(db);

// Создаём новый диалог
int conversationId =
    await conversationService.CreateConversationAsync();

using HttpClient http = new();

ProviderConnectionService providerConnectionService =
    new(db);

ProviderManager providerManager =
    new(db, http, gigaOptions);

ConversationEntity? startupConversation =
    await conversationService.GetConversationAsync(conversationId);
IAIProvider startupProvider =
    await providerManager.GetProviderForConversationAsync(conversationId);

Console.WriteLine($"Провайдер: {startupProvider.Name}");
Console.WriteLine($"Модель: {startupConversation?.Model}");

Console.Write("Название нового диалога (Enter — по умолчанию): ");

string? initialTitle = Console.ReadLine()?.Trim();

if (!string.IsNullOrWhiteSpace(initialTitle))
{
    await conversationService.RenameConversationAsync(
        conversationId,
        initialTitle);
}

// Загружаем историю
List<ChatMessage> history =
    await conversationService.GetMessagesAsync(conversationId);

Console.WriteLine($"\nID диалога: {conversationId}");

Console.WriteLine("\nДля просмотра списка команд введите /help.");


while (true)
{
    Console.Write("\n> ");

    string? input = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(input))
    {
        continue;
    }

    // Выбор провайдера по имени, без ручного создания подключений.
    if (input.Equals("/provider", StringComparison.OrdinalIgnoreCase))
    {
        ConversationEntity? activeConversation =
            await conversationService.GetConversationAsync(conversationId);
        IAIProvider activeProvider =
            await providerManager.GetProviderForConversationAsync(conversationId);

        Console.WriteLine($"Провайдер: {activeProvider.Name}");
        Console.WriteLine($"Модель: {activeConversation?.Model}");
        Console.WriteLine("Доступно: /provider GigaChat, /provider ProxyAPI");
        continue;
    }

    if (input.StartsWith("/provider ", StringComparison.OrdinalIgnoreCase))
    {
        string providerName = input["/provider ".Length..].Trim();

        try
        {
            if (providerName.Equals("GigaChat", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(gigaOptions.AuthKey))
                {
                    Console.WriteLine("Не найдена переменная окружения GIGACHAT_AUTH_KEY.");
                    continue;
                }

                await conversationService.SetProviderConnectionAsync(conversationId, null);
                await conversationService.UpdateSettingsAsync(
                    conversationId, model: gigaOptions.DefaultModel);
                await conversationService.ResetGenerationSettingsAsync(conversationId);

                Console.WriteLine("Провайдер: GigaChat");
                Console.WriteLine($"Модель: {gigaOptions.DefaultModel}");
                continue;
            }

            if (providerName.Equals("ProxyAPI", StringComparison.OrdinalIgnoreCase))
            {
                string variableName = proxyOptions.ApiKeyEnvironmentVariable;
                if (string.IsNullOrWhiteSpace(variableName) ||
                    string.IsNullOrWhiteSpace(
                        Environment.GetEnvironmentVariable(variableName)))
                {
                    Console.WriteLine($"Не найдена переменная окружения {variableName}.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(proxyOptions.DefaultModel))
                {
                    Console.WriteLine("В настройках ProxyAPI не задана модель по умолчанию.");
                    continue;
                }

                List<ProviderConnectionEntity> connections =
                    await providerConnectionService.GetConnectionsAsync();

                ProviderConnectionEntity? connection = connections
                    .FirstOrDefault(c =>
                        c.ProviderType == "OpenAICompatible" &&
                        c.Name.Equals(proxyOptions.Name, StringComparison.OrdinalIgnoreCase));

                if (connection == null)
                {
                    int id = await providerConnectionService.AddConnectionAsync(
                        proxyOptions.Name,
                        "OpenAICompatible",
                        proxyOptions.BaseUrl,
                        variableName,
                        proxyOptions.DefaultModel);
                    connection = await providerConnectionService.GetConnectionAsync(id);
                }
                else
                {
                    // Синхронизируем старую запись с актуальными Options.
                    connection.BaseUrl = proxyOptions.BaseUrl.TrimEnd('/');
                    connection.ApiKeyEnvironmentVariable = variableName;
                    connection.DefaultModel = proxyOptions.DefaultModel;
                    await db.SaveChangesAsync();
                }

                if (connection == null)
                {
                    throw new InvalidOperationException("Не удалось создать подключение ProxyAPI.");
                }

                await conversationService.SetProviderConnectionAsync(
                    conversationId, connection.Id);
                await conversationService.UpdateSettingsAsync(
                    conversationId, model: proxyOptions.DefaultModel);
                await conversationService.ResetGenerationSettingsAsync(conversationId);

                Console.WriteLine("Провайдер: ProxyAPI");
                Console.WriteLine($"Модель: {proxyOptions.DefaultModel}");
                continue;
            }

            Console.WriteLine("Неизвестный провайдер. Используй /provider GigaChat или /provider ProxyAPI.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка переключения провайдера: {ex.Message}");
        }

        continue;
    }

    if (input.Equals("/help", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("""

        ===== UniversalAI — Справка =====

        ДИАЛОГИ:
          /new                    Создать диалог с вводом названия
          /new НАЗВАНИЕ           Создать диалог с указанным названием
          /rename НАЗВАНИЕ        Переименовать текущий диалог
          /history                Показать историю диалогов
          /open ID                Открыть диалог
          /reset                  Очистить историю текущего диалога

        НАСТРОЙКИ:
          /prompt                 Показать системный промпт
          /prompt ТЕКСТ           Изменить системный промпт
          /model                  Показать текущую модель
          /model НАЗВАНИЕ         Изменить модель
          /temperature            Показать температуру
          /temperature 0.7        Изменить температуру (0–2)
          /max_tokens             Показать лимит токенов ответа
          /max_tokens 4096        Установить лимит токенов ответа
          /top_p 0.9              Установить Top P
          /frequency_penalty 0.5  Установить Frequency Penalty
          /presence_penalty 0.5   Установить Presence Penalty
          /seed 42                Установить Seed

        ПРОВАЙДЕРЫ:
          /provider              Показать текущего провайдера
          /provider GigaChat     Выбрать GigaChat и его модель по умолчанию
          /provider ProxyAPI     Выбрать ProxyAPI и его модель по умолчанию
          /models                Получить список моделей

        REASONING:
          /reasoning              Показать текущий режим
          /reasoning low          Установить низкий уровень
          /reasoning medium       Средний уровень (если поддерживается)
          /reasoning high         Установить высокий уровень
          /reasoning max          Максимальный уровень (DeepSeek)
          /reasoning none         Отключить reasoning в API (DeepSeek)
          /reasoning off          Отключить reasoning
          /reasoning_budget 4096  Установить бюджет reasoning-токенов

        ДОПОЛНИТЕЛЬНО:
          /settings               Показать настройки диалога
          /defaults               Сбросить настройки генерации
          /help                   Показать эту справку
          /exit                   Завершить работу

        Примеры:
          /model GigaChat-2
          /temperature 0.5
          /max_tokens 4096
          /reasoning high
          /open 3

        =================================
        """);

        continue;
    }

    if (input.Equals("/models", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            IAIProvider currentProvider =
                await providerManager.GetProviderForConversationAsync(
                    conversationId);

            if (currentProvider is OpenAICompatibleProvider compatibleProvider)
            {
                List<OpenAICompatibleModel> models =
                    await compatibleProvider.GetModelsAsync();

                Console.WriteLine(
                    $"\nДоступные модели {currentProvider.Name}:");

                foreach (OpenAICompatibleModel model in models)
                {
                    Console.WriteLine($"  {model.Id}");
                }

                Console.WriteLine($"\nВсего моделей: {models.Count}");
            }
            else
            {
                Console.WriteLine(
                    $"Получение списка моделей пока не реализовано для {currentProvider.Name}.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка: {ex.Message}");
        }

        continue;
    }


    // Выход
    if (input.Equals("/exit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    // Список диалогов
    if (input.Equals("/history", StringComparison.OrdinalIgnoreCase))
    {
        List<ConversationEntity> conversations =
            await conversationService.GetConversationsAsync();

        Console.WriteLine("\n--- История диалогов ---");

        if (conversations.Count == 0)
        {
            Console.WriteLine("Диалогов пока нет.");
        }
        else
        {
            foreach (ConversationEntity conversation in conversations)
            {
                Console.WriteLine(
                    $"ID: {conversation.Id} | " +
                    $"{conversation.Title} | " +
                    $"{conversation.UpdatedAt.ToLocalTime():dd.MM.yyyy HH:mm}");
            }
        }

        continue;
    }

    // Создание нового диалога

    if (input.Equals("/new", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("/new ", StringComparison.OrdinalIgnoreCase))
    {
        string title = input.Length > 4
            ? input[4..].Trim()
            : "";

        if (string.IsNullOrWhiteSpace(title))
        {
            Console.Write("Название диалога (Enter — по умолчанию): ");
            title = Console.ReadLine()?.Trim() ?? "";
        }

        conversationId =
            await conversationService.CreateConversationAsync();

        if (!string.IsNullOrWhiteSpace(title))
        {
            await conversationService.RenameConversationAsync(
                conversationId,
                title);
        }

        history.Clear();

        ConversationEntity? newConversation =
            await conversationService.GetConversationAsync(conversationId);

        Console.WriteLine($"\nСоздан диалог ID: {conversationId}");
        Console.WriteLine($"Название: {newConversation?.Title}");

        continue;
    }


    if (input.Equals("/rename", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("/rename ", StringComparison.OrdinalIgnoreCase))
    {
        string title = input.Length > "/rename".Length
            ? input["/rename".Length..].Trim()
            : "";

        if (string.IsNullOrWhiteSpace(title))
        {
            Console.Write("Новое название диалога: ");
            title = Console.ReadLine()?.Trim() ?? "";
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            Console.WriteLine("Название не может быть пустым.");
            continue;
        }

        await conversationService.RenameConversationAsync(
            conversationId,
            title);

        Console.WriteLine($"Диалог переименован: {title}");

        continue;
    }


    // Открытие существующего диалога
    if (input.StartsWith("/open ", StringComparison.OrdinalIgnoreCase))
    {
        string idText = input["/open ".Length..].Trim();

        if (!int.TryParse(idText, out int requestedId))
        {
            Console.WriteLine("Использование: /open ID");
            continue;
        }

        bool exists =
            await conversationService.ConversationExistsAsync(requestedId);

        if (!exists)
        {
            Console.WriteLine("Диалог с таким ID не найден.");
            continue;
        }

        conversationId = requestedId;

        history = await conversationService.GetMessagesAsync(
            conversationId);

        ConversationEntity? openedConversation =
    await conversationService.GetConversationAsync(conversationId);

        Console.WriteLine($"\nДиалог открыт");
        Console.WriteLine($"ID: {conversationId}");
        Console.WriteLine($"Название: {openedConversation?.Title ?? "Без названия"}");
        Console.WriteLine($"Загружено сообщений: {history.Count}");

        continue;
    }

    // Очистка истории
    if (input.Equals("/reset", StringComparison.OrdinalIgnoreCase))
    {
        await conversationService.ClearConversationAsync(conversationId);

        history.Clear();

        Console.WriteLine("История текущего диалога очищена.");
        continue;
    }

    // Получаем настройки текущего диалога
    ConversationEntity? currentConversation =
        await conversationService.GetConversationAsync(conversationId);

    if (currentConversation == null)
    {
        Console.WriteLine("Текущий диалог не найден.");
        continue;
    }

    // Показать системный промпт
    if (input.Equals("/prompt", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("\nТекущий системный промпт:");
        Console.WriteLine(currentConversation.SystemPrompt);
        continue;
    }

    // Изменить системный промпт
    if (input.StartsWith("/prompt ", StringComparison.OrdinalIgnoreCase))
    {
        string newPrompt = input["/prompt ".Length..].Trim();

        if (string.IsNullOrWhiteSpace(newPrompt))
        {
            Console.WriteLine("Промпт не может быть пустым.");
            continue;
        }

        await conversationService.UpdateSettingsAsync(
            conversationId,
            systemPrompt: newPrompt);

        Console.WriteLine("Системный промпт обновлён.");
        continue;
    }

    // Показать модель
    if (input.Equals("/model", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"Текущая модель: {currentConversation.Model}");
        continue;
    }

    // Изменить модель
    if (input.StartsWith("/model ", StringComparison.OrdinalIgnoreCase))
    {
        string newModel = input["/model ".Length..].Trim();

        if (string.IsNullOrWhiteSpace(newModel))
        {
            Console.WriteLine("Название модели не может быть пустым.");
            continue;
        }

        await conversationService.UpdateSettingsAsync(
            conversationId,
            model: newModel);

        Console.WriteLine($"Модель изменена на: {newModel}");
        continue;
    }

    // Показать температуру
    if (input.Equals("/temperature", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine(
            $"Текущая температура: {currentConversation.Temperature}");

        continue;
    }

    // Изменить температуру
    if (input.StartsWith("/temperature ", StringComparison.OrdinalIgnoreCase))
    {
        string value = input["/temperature ".Length..].Trim();

        if (!double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double newTemperature))
        {
            Console.WriteLine(
                "Введите число. Например: /temperature 0.5");

            continue;
        }

        if (newTemperature is < 0 or > 2)
        {
            Console.WriteLine(
                "Температура должна находиться в диапазоне от 0 до 2.");

            continue;
        }

        await conversationService.UpdateSettingsAsync(
            conversationId,
            temperature: newTemperature);

        Console.WriteLine(
            $"Температура изменена на: {newTemperature}");

        continue;
    }

    // Показать максимальное количество токенов ответа
    if (input.Equals("/max_tokens", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"Текущий MaxTokens: {currentConversation.MaxTokens}");
        continue;
    }

    // Изменить максимальное количество токенов ответа
    if (input.StartsWith("/max_tokens ", StringComparison.OrdinalIgnoreCase))
    {
        string value = input["/max_tokens ".Length..].Trim();

        if (!int.TryParse(value, out int maxTokens) || maxTokens < 1)
        {
            Console.WriteLine("Использование: /max_tokens 4096 (целое число больше нуля)");
            continue;
        }

        await conversationService.UpdateSettingsAsync(
            conversationId,
            maxTokens: maxTokens);

        Console.WriteLine($"MaxTokens установлен: {maxTokens}");
        continue;
    }

    if (input.Equals("/settings", StringComparison.OrdinalIgnoreCase))
    {
        currentConversation =
            await conversationService.GetConversationAsync(conversationId);

        if (currentConversation == null)
        {
            Console.WriteLine("Диалог не найден.");
            continue;
        }

        Console.WriteLine();
        Console.WriteLine("=== НАСТРОЙКИ ДИАЛОГА ===");
        IAIProvider settingsProvider = await providerManager.GetProviderForConversationAsync(conversationId);
        Console.WriteLine($"Провайдер: {settingsProvider.Name}");
        Console.WriteLine($"Модель: {currentConversation.Model}");
        Console.WriteLine($"Temperature: {currentConversation.Temperature}");
        Console.WriteLine($"Max tokens: {currentConversation.MaxTokens}");
        Console.WriteLine($"Top P: {currentConversation.TopP?.ToString() ?? "не задан"}");
        Console.WriteLine($"Frequency penalty: {currentConversation.FrequencyPenalty?.ToString() ?? "не задан"}");
        Console.WriteLine($"Presence penalty: {currentConversation.PresencePenalty?.ToString() ?? "не задан"}");
        Console.WriteLine($"Seed: {currentConversation.Seed?.ToString() ?? "не задан"}");
        Console.WriteLine($"Reasoning: {(string.IsNullOrWhiteSpace(currentConversation.ReasoningEffort) ? "не задан" : currentConversation.ReasoningEffort)}");
        Console.WriteLine($"Reasoning budget: {currentConversation.ReasoningBudgetTokens?.ToString() ?? "не задан"}");
        Console.WriteLine($"System prompt: {currentConversation.SystemPrompt}");

        Console.WriteLine("========================");
        Console.WriteLine();

        continue;
    }

    if (input.Equals("/reasoning", StringComparison.OrdinalIgnoreCase) ||
    input.StartsWith("/reasoning ", StringComparison.OrdinalIgnoreCase))
    {
        string[] parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 1)
        {
            Console.WriteLine(
                $"Текущий reasoning: {(string.IsNullOrWhiteSpace(currentConversation.ReasoningEffort) ? "не задан" : currentConversation.ReasoningEffort)}");
            continue;
        }

        string value = parts[1].ToLowerInvariant();

        if (value is not "none" and
            not "low" and
            not "medium" and
            not "high" and
            not "xhigh" and
            not "max" and
            not "off")
        {
            Console.WriteLine(
                "Использование: /reasoning none|low|medium|high|xhigh|max|off");

            continue;
        }

        await conversationService.UpdateSettingsAsync(
            conversationId,
            reasoningEffort: value == "off" ? "" : value);

        currentConversation =
            await conversationService.GetConversationAsync(conversationId);

        Console.WriteLine($"Reasoning: {(string.IsNullOrWhiteSpace(currentConversation?.ReasoningEffort) ? "не задан" : currentConversation.ReasoningEffort)}");

        continue;
    }

    if (input.StartsWith("/reasoning_budget", StringComparison.OrdinalIgnoreCase))
    {
        string[] parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 2 ||
            !int.TryParse(parts[1], out int budget) ||
            budget < 1 ||
            budget > 100000)
        {
            Console.WriteLine("Использование: /reasoning_budget 4096");
            continue;
        }

        await conversationService.UpdateSettingsAsync(
            conversationId,
            reasoningBudgetTokens: budget);

        Console.WriteLine($"Reasoning budget установлен: {budget}");

        continue;
    }

    if (input.StartsWith("/top_p", StringComparison.OrdinalIgnoreCase))
    {
        string[] parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 2 ||
            !double.TryParse(
                parts[1],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double value) ||
            value < 0 ||
            value > 1)
        {
            Console.WriteLine("Использование: /top_p 0.9");
            continue;
        }

        await conversationService.UpdateSettingsAsync(
            conversationId,
            topP: value);

        Console.WriteLine($"Top P установлен: {value}");

        continue;
    }

    if (input.StartsWith("/frequency_penalty", StringComparison.OrdinalIgnoreCase))
    {
        string[] parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 2 ||
            !double.TryParse(parts[1], NumberStyles.Float,
                CultureInfo.InvariantCulture, out double value) ||
            value < -2 || value > 2)
        {
            Console.WriteLine("Использование: /frequency_penalty 0.5");
            continue;
        }

        await conversationService.UpdateSettingsAsync(
            conversationId,
            frequencyPenalty: value);

        Console.WriteLine($"Frequency penalty установлен: {value}");

        continue;
    }

    if (input.StartsWith("/presence_penalty", StringComparison.OrdinalIgnoreCase))
    {
        string[] parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 2 ||
            !double.TryParse(parts[1], NumberStyles.Float,
                CultureInfo.InvariantCulture, out double value) ||
            value < -2 || value > 2)
        {
            Console.WriteLine("Использование: /presence_penalty 0.5");
            continue;
        }

        await conversationService.UpdateSettingsAsync(
            conversationId,
            presencePenalty: value);

        Console.WriteLine($"Presence penalty установлен: {value}");

        continue;
    }

    if (input.StartsWith("/seed", StringComparison.OrdinalIgnoreCase))
    {
        string[] parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 2 ||
            !int.TryParse(parts[1], out int seed) ||
            seed < 0)
        {
            Console.WriteLine("Использование: /seed 42");
            continue;
        }

        await conversationService.UpdateSettingsAsync(
            conversationId,
            seed: seed);

        Console.WriteLine($"Seed установлен: {seed}");

        continue;
    }

    if (input.Equals("/defaults", StringComparison.OrdinalIgnoreCase))
    {
        await conversationService.ResetGenerationSettingsAsync(
            conversationId);

        currentConversation =
            await conversationService.GetConversationAsync(conversationId);

        Console.WriteLine("Настройки генерации сброшены.");

        continue;
    }

    // Создаём сообщение пользователя
    ChatMessage userMessage = new()
    {
        Role = "user",
        Content = input
    };

    // Добавляем сообщение в историю для формирования запроса.
    // В базу сохраним его только после успешного ответа.
    history.Add(userMessage);

    // Формируем сообщения для AI
    List<ChatMessage> requestMessages =
    [
        new ChatMessage
        {
            Role = "system",
            Content = currentConversation.SystemPrompt
        },
        .. history,
    ];

    // Формируем запрос    
    ChatRequest request = new()
    {
        Model = currentConversation.Model,
        Messages = requestMessages,

        Temperature = currentConversation.Temperature,
        MaxTokens = currentConversation.MaxTokens,

        TopP = currentConversation.TopP,
        FrequencyPenalty = currentConversation.FrequencyPenalty,
        PresencePenalty = currentConversation.PresencePenalty,

        Seed = currentConversation.Seed,

        ReasoningEffort = currentConversation.ReasoningEffort,
        ReasoningBudgetTokens = currentConversation.ReasoningBudgetTokens
    };

    try
    {
        IAIProvider currentProvider =
            await providerManager.GetProviderForConversationAsync(
                conversationId);

        ChatResult result =
            await currentProvider.SendMessageAsync(request);

        Console.WriteLine("\nОтвет модели:");
        Console.WriteLine(result.Content);

        ChatMessage assistantMessage = new()
        {
            Role = "assistant",
            Content = result.Content
        };

        // Сохраняем сообщение пользователя
        await conversationService.SaveMessageAsync(
            conversationId,
            userMessage);

        // Сохраняем ответ ассистента
        await conversationService.SaveMessageAsync(
            conversationId,
            assistantMessage);

        history.Add(assistantMessage);

        Console.WriteLine("\n--- Статистика ---");
        Console.WriteLine($"Провайдер: {currentProvider.Name}");
        Console.WriteLine($"Модель: {request.Model}");
        Console.WriteLine($"Finish reason: {result.FinishReason}");
        Console.WriteLine($"Prompt tokens: {result.PromptTokens}");
        Console.WriteLine($"Completion tokens: {result.CompletionTokens}");
        Console.WriteLine($"Total tokens: {result.TotalTokens}");
    }
    catch (NotSupportedException ex)
    {
        Console.WriteLine("\nНастройка не поддерживается:");
        Console.WriteLine(ex.Message);

        history.Remove(userMessage);
    }

    catch (Exception ex)
    {
        Console.WriteLine("\nОшибка при обращении к AI:");
        Console.WriteLine(ex.Message);

        history.Remove(userMessage);

        Console.WriteLine("Сообщение не сохранено.");
    }
}

Console.WriteLine("\nРабота завершена.");
