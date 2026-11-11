namespace UniversalAI.ConsoleApp.Commands;

public sealed class HelpCommandHandler : IConsoleCommandHandler
{
    public Task<bool> TryHandleAsync(
        string input,
        ConsoleChatSession session,
        CancellationToken cancellationToken = default)
    {
        if (!input.Equals("/help", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(false);
        }

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

            АГЕНТЫ:
              /agent                 Показать текущий и доступные агенты
              /agent tools           Выбрать агента с инструментами
              /agent direct          Выбрать прямой чат без инструментов

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
              /paste                  Вставить многострочный текст; завершить строкой /send
              /tokens                 Статистика токенов и стоимости истории
              /settings               Показать настройки диалога
              /defaults               Сбросить настройки генерации
              /help                   Показать эту справку
              /exit                   Завершить работу

            ИНСТРУМЕНТЫ АГЕНТА:
              Спросить текущее время, попросить вычислить выражение
              или бросить кубик. Агент сам выберет инструмент.

            Примеры:
              /provider ProxyAPI
              /model anthropic/claude-opus-5-5
              /reasoning high
              /model GigaChat-2
              /temperature 0.5
              /max_tokens 4096
              /reasoning high
              /open 3
              Который сейчас час?
              Сколько минут прошло с полуночи? Узнай время и посчитай.

            =================================
            """);

        return Task.FromResult(true);
    }
}
