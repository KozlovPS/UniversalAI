using System.Globalization;
using UniversalAI.Data.Entities;
using UniversalAI.Providers;
using UniversalAI.Services;
using UniversalAI.Services.Providers;

namespace UniversalAI.ConsoleApp.Commands;

public sealed class GenerationSettingsCommandHandler : IConsoleCommandHandler
{
    private readonly ConversationService _conversations;
    private readonly ProviderManager _providers;

    public GenerationSettingsCommandHandler(
        ConversationService conversations,
        ProviderManager providers)
    {
        _conversations = conversations;
        _providers = providers;
    }

    public async Task<bool> TryHandleAsync(
        string input,
        ConsoleChatSession session,
        CancellationToken cancellationToken = default)
    {
        if (!IsSettingsCommand(input))
        {
            return false;
        }

        ConversationEntity? conversation =
            await _conversations.GetConversationAsync(session.ConversationId);
        if (conversation == null)
        {
            Console.WriteLine("Текущий диалог не найден.");
            return true;
        }

        if (input.Equals("/prompt", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("\nТекущий системный промпт:");
            Console.WriteLine(conversation.SystemPrompt);
            return true;
        }

        if (input.StartsWith("/prompt ", StringComparison.OrdinalIgnoreCase))
        {
            string prompt = input["/prompt ".Length..].Trim();
            if (string.IsNullOrWhiteSpace(prompt))
            {
                Console.WriteLine("Промпт не может быть пустым.");
                return true;
            }

            await _conversations.UpdateSettingsAsync(
                session.ConversationId,
                systemPrompt: prompt);
            Console.WriteLine("Системный промпт обновлён.");
            return true;
        }

        if (input.Equals("/model", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Текущая модель: {conversation.Model}");
            return true;
        }

        if (input.StartsWith("/model ", StringComparison.OrdinalIgnoreCase))
        {
            string model = input["/model ".Length..].Trim();
            if (string.IsNullOrWhiteSpace(model))
            {
                Console.WriteLine("Название модели не может быть пустым.");
                return true;
            }

            await _conversations.UpdateSettingsAsync(session.ConversationId, model: model);
            Console.WriteLine($"Модель изменена на: {model}");
            return true;
        }

        if (input.Equals("/temperature", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Текущая температура: {conversation.Temperature}");
            return true;
        }

        if (input.StartsWith("/temperature ", StringComparison.OrdinalIgnoreCase))
        {
            string value = input["/temperature ".Length..].Trim();
            if (!TryParseDouble(value, out double temperature))
            {
                Console.WriteLine("Введите число. Например: /temperature 0.5");
                return true;
            }

            if (temperature is < 0 or > 2)
            {
                Console.WriteLine("Температура должна находиться в диапазоне от 0 до 2.");
                return true;
            }

            await _conversations.UpdateSettingsAsync(
                session.ConversationId,
                temperature: temperature);
            Console.WriteLine($"Температура изменена на: {temperature}");
            return true;
        }

        if (input.Equals("/max_tokens", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Текущий MaxTokens: {conversation.MaxTokens}");
            return true;
        }

        if (input.StartsWith("/max_tokens ", StringComparison.OrdinalIgnoreCase))
        {
            string value = input["/max_tokens ".Length..].Trim();
            if (!int.TryParse(value, out int maxTokens) || maxTokens < 1)
            {
                Console.WriteLine("Использование: /max_tokens 4096 (целое число больше нуля)");
                return true;
            }

            await _conversations.UpdateSettingsAsync(
                session.ConversationId,
                maxTokens: maxTokens);
            Console.WriteLine($"MaxTokens установлен: {maxTokens}");
            return true;
        }

        if (input.Equals("/settings", StringComparison.OrdinalIgnoreCase))
        {
            await ShowSettingsAsync(session.ConversationId, conversation);
            return true;
        }

        if (input.Equals("/reasoning", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("/reasoning ", StringComparison.OrdinalIgnoreCase))
        {
            await UpdateReasoningAsync(input, session.ConversationId, conversation);
            return true;
        }

        if (input.StartsWith("/reasoning_budget", StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = Split(input);
            if (parts.Length != 2 ||
                !int.TryParse(parts[1], out int budget) ||
                budget is < 1 or > 100000)
            {
                Console.WriteLine("Использование: /reasoning_budget 4096");
                return true;
            }

            await _conversations.UpdateSettingsAsync(
                session.ConversationId,
                reasoningBudgetTokens: budget);
            Console.WriteLine($"Reasoning budget установлен: {budget}");
            return true;
        }

        if (input.StartsWith("/top_p", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseSingleValue(input, out double value) || value is < 0 or > 1)
            {
                Console.WriteLine("Использование: /top_p 0.9");
                return true;
            }

            await _conversations.UpdateSettingsAsync(session.ConversationId, topP: value);
            Console.WriteLine($"Top P установлен: {value}");
            return true;
        }

        if (input.StartsWith("/frequency_penalty", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseSingleValue(input, out double value) || value is < -2 or > 2)
            {
                Console.WriteLine("Использование: /frequency_penalty 0.5");
                return true;
            }

            await _conversations.UpdateSettingsAsync(
                session.ConversationId,
                frequencyPenalty: value);
            Console.WriteLine($"Frequency penalty установлен: {value}");
            return true;
        }

        if (input.StartsWith("/presence_penalty", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseSingleValue(input, out double value) || value is < -2 or > 2)
            {
                Console.WriteLine("Использование: /presence_penalty 0.5");
                return true;
            }

            await _conversations.UpdateSettingsAsync(
                session.ConversationId,
                presencePenalty: value);
            Console.WriteLine($"Presence penalty установлен: {value}");
            return true;
        }

        if (input.StartsWith("/seed", StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = Split(input);
            if (parts.Length != 2 || !int.TryParse(parts[1], out int seed) || seed < 0)
            {
                Console.WriteLine("Использование: /seed 42");
                return true;
            }

            await _conversations.UpdateSettingsAsync(session.ConversationId, seed: seed);
            Console.WriteLine($"Seed установлен: {seed}");
            return true;
        }

        if (input.Equals("/defaults", StringComparison.OrdinalIgnoreCase))
        {
            await _conversations.ResetGenerationSettingsAsync(session.ConversationId);
            Console.WriteLine("Настройки генерации сброшены.");
            return true;
        }

        return false;
    }

    private async Task ShowSettingsAsync(
        int conversationId,
        ConversationEntity conversation)
    {
        IAIProvider provider =
            await _providers.GetProviderForConversationAsync(conversationId);

        Console.WriteLine();
        Console.WriteLine("=== НАСТРОЙКИ ДИАЛОГА ===");
        Console.WriteLine($"Провайдер: {provider.Name}");
        Console.WriteLine($"Модель: {conversation.Model}");
        Console.WriteLine($"Temperature: {conversation.Temperature}");
        Console.WriteLine($"Max tokens: {conversation.MaxTokens}");
        Console.WriteLine($"Top P: {conversation.TopP?.ToString() ?? "не задан"}");
        Console.WriteLine($"Frequency penalty: {conversation.FrequencyPenalty?.ToString() ?? "не задан"}");
        Console.WriteLine($"Presence penalty: {conversation.PresencePenalty?.ToString() ?? "не задан"}");
        Console.WriteLine($"Seed: {conversation.Seed?.ToString() ?? "не задан"}");
        Console.WriteLine($"Reasoning: {(string.IsNullOrWhiteSpace(conversation.ReasoningEffort) ? "не задан" : conversation.ReasoningEffort)}");
        Console.WriteLine($"Reasoning budget: {conversation.ReasoningBudgetTokens?.ToString() ?? "не задан"}");
        Console.WriteLine($"System prompt: {conversation.SystemPrompt}");
        Console.WriteLine("========================");
        Console.WriteLine();
    }

    private async Task UpdateReasoningAsync(
        string input,
        int conversationId,
        ConversationEntity conversation)
    {
        string[] parts = Split(input);
        if (parts.Length == 1)
        {
            string current = string.IsNullOrWhiteSpace(conversation.ReasoningEffort)
                ? "не задан"
                : conversation.ReasoningEffort;
            Console.WriteLine($"Текущий reasoning: {current}");
            return;
        }

        string value = parts[1].ToLowerInvariant();
        if (value is not "none" and not "low" and not "medium" and not "high" and
            not "xhigh" and not "max" and not "off")
        {
            Console.WriteLine("Использование: /reasoning none|low|medium|high|xhigh|max|off");
            return;
        }

        await _conversations.UpdateSettingsAsync(
            conversationId,
            reasoningEffort: value == "off" ? "" : value);

        Console.WriteLine($"Reasoning: {(value == "off" ? "не задан" : value)}");
    }

    private static bool IsSettingsCommand(string input) =>
        input.Equals("/prompt", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("/prompt ", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("/model", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("/model ", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("/temperature", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("/temperature ", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("/max_tokens", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("/max_tokens ", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("/settings", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("/reasoning", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("/reasoning ", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("/reasoning_budget", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("/top_p", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("/frequency_penalty", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("/presence_penalty", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("/seed", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("/defaults", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseSingleValue(string input, out double value)
    {
        string[] parts = Split(input);
        value = default;
        return parts.Length == 2 && TryParseDouble(parts[1], out value);
    }

    private static bool TryParseDouble(string input, out double value) =>
        double.TryParse(
            input,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);

    private static string[] Split(string input) =>
        input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
