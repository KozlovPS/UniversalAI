using Microsoft.EntityFrameworkCore;
using UniversalAI.Core;
using UniversalAI.Data;
using UniversalAI.Data.Entities;

namespace UniversalAI.Services;

public class ConversationService
{
    private readonly AppDbContext _db;

    public ConversationService(AppDbContext db)
    {
        _db = db;
    }

    // Создание нового диалога
    public async Task<int> CreateConversationAsync(
        CancellationToken cancellationToken = default)
    {
        ConversationEntity conversation = new()
        {
            Title = "Новый диалог",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Conversations.Add(conversation);

        await _db.SaveChangesAsync(cancellationToken);

        return conversation.Id;
    }

    // Сохранение сообщения
    public async Task SaveMessageAsync(
        int conversationId,
        ChatMessage message,
        CancellationToken cancellationToken = default)
    {
        ConversationEntity? conversation = await _db.Conversations
            .FirstOrDefaultAsync(
                c => c.Id == conversationId,
                cancellationToken);

        if (conversation == null)
        {
            throw new InvalidOperationException(
                $"Диалог с ID {conversationId} не найден.");
        }

        ChatMessageEntity entity = new()
        {
            ConversationId = conversationId,
            Role = message.Role,
            Content = message.Content,
            CreatedAt = DateTime.UtcNow
        };

        _db.ChatMessages.Add(entity);

        // Заголовок формируем из первого сообщения пользователя
        if (message.Role == "user" &&
            conversation.Title == "Новый диалог")
        {
            conversation.Title = message.Content.Length > 50
                ? message.Content[..50] + "..."
                : message.Content;
        }

        conversation.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
    }

    // Загрузка истории диалога
    public async Task<List<ChatMessage>> GetMessagesAsync(
        int conversationId,
        CancellationToken cancellationToken = default)
    {
        return await _db.ChatMessages
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .Select(m => new ChatMessage
            {
                Role = m.Role,
                Content = m.Content
            })
            .ToListAsync(cancellationToken);
    }

    // Очистка истории диалога
    public async Task ClearConversationAsync(
        int conversationId,
        CancellationToken cancellationToken = default)
    {
        ConversationEntity? conversation = await _db.Conversations
            .FirstOrDefaultAsync(
                c => c.Id == conversationId,
                cancellationToken);

        if (conversation == null)
        {
            throw new InvalidOperationException(
                $"Диалог с ID {conversationId} не найден.");
        }

        List<ChatMessageEntity> messages = await _db.ChatMessages
            .Where(m => m.ConversationId == conversationId)
            .ToListAsync(cancellationToken);

        _db.ChatMessages.RemoveRange(messages);

        conversation.Title = "Новый диалог";
        conversation.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
    }

    // Получение списка всех диалогов
    public async Task<List<ConversationEntity>> GetConversationsAsync(
        CancellationToken cancellationToken = default)
    {
        return await _db.Conversations
            .AsNoTracking()
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(cancellationToken);
    }

    // Проверка существования диалога
    public async Task<bool> ConversationExistsAsync(
        int conversationId,
        CancellationToken cancellationToken = default)
    {
        return await _db.Conversations
            .AnyAsync(
                c => c.Id == conversationId,
                cancellationToken);
    }

    public async Task<ConversationEntity?> GetConversationAsync(int id)
    {
        return await _db.Conversations
            .FirstOrDefaultAsync(c => c.Id == id);
    }

    public async Task UpdateSettingsAsync(
    int id,
    string? systemPrompt = null,
    string? model = null,
    double? temperature = null,
    int? maxTokens = null,
    double? topP = null,
    double? frequencyPenalty = null,
    double? presencePenalty = null,
    int? seed = null,
    string? reasoningEffort = null,
    int? reasoningBudgetTokens = null)
    {
        ConversationEntity? conversation =
            await _db.Conversations.FirstOrDefaultAsync(c => c.Id == id);

        if (conversation == null)
        {
            throw new InvalidOperationException("Диалог не найден.");
        }

        if (systemPrompt != null)
        {
            conversation.SystemPrompt = systemPrompt;
        }

        if (model != null)
        {
            conversation.Model = model;
        }

        if (temperature.HasValue)
        {
            conversation.Temperature = temperature.Value;
        }

        if (maxTokens.HasValue)
        {
            conversation.MaxTokens = maxTokens.Value;
        }

        if (topP.HasValue)
        {
            conversation.TopP = topP.Value;
        }

        if (frequencyPenalty.HasValue)
        {
            conversation.FrequencyPenalty = frequencyPenalty.Value;
        }

        if (presencePenalty.HasValue)
        {
            conversation.PresencePenalty = presencePenalty.Value;
        }

        if (seed.HasValue)
        {
            conversation.Seed = seed.Value;
        }

        if (reasoningEffort != null)
        {
            conversation.ReasoningEffort =
                string.IsNullOrWhiteSpace(reasoningEffort)
                    ? null
                    : reasoningEffort;
        }

        if (reasoningBudgetTokens.HasValue)
        {
            conversation.ReasoningBudgetTokens = reasoningBudgetTokens.Value;
        }

        conversation.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

    public async Task ResetGenerationSettingsAsync(int id)
    {
        ConversationEntity? conversation =
            await _db.Conversations.FirstOrDefaultAsync(c => c.Id == id);

        if (conversation == null)
        {
            throw new InvalidOperationException("Диалог не найден.");
        }

        conversation.MaxTokens = 1000;
        conversation.Temperature = 0.7;
        conversation.TopP = null;
        conversation.FrequencyPenalty = null;
        conversation.PresencePenalty = null;
        conversation.Seed = null;
        conversation.ReasoningEffort = null;
        conversation.ReasoningBudgetTokens = null;

        conversation.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }


    public async Task SetProviderConnectionAsync(
        int conversationId,
        int? providerConnectionId)
    {
        ConversationEntity? conversation =
            await _db.Conversations.FirstOrDefaultAsync(
                c => c.Id == conversationId);

        if (conversation == null)
        {
            throw new InvalidOperationException("Диалог не найден.");
        }

        if (providerConnectionId.HasValue)
        {
            bool exists = await _db.ProviderConnections.AnyAsync(
                c => c.Id == providerConnectionId.Value);

            if (!exists)
            {
                throw new InvalidOperationException(
                    "Подключение не найдено.");
            }
        }

        conversation.ProviderConnectionId = providerConnectionId;
        conversation.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

    public async Task RenameConversationAsync(
    int conversationId,
    string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Название диалога не может быть пустым.");
        }

        ConversationEntity? conversation =
            await _db.Conversations.FindAsync(conversationId);

        if (conversation == null)
        {
            throw new InvalidOperationException("Диалог не найден.");
        }

        conversation.Title = title.Trim();
        conversation.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

}