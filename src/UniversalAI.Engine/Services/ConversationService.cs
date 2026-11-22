using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using UniversalAI.Core;
using UniversalAI.Data;
using UniversalAI.Data.Entities;

namespace UniversalAI.Services;

public class ConversationService : IContextMemoryStore
{
    public async Task<MessagePage> GetMessagePageAsync(int conversationId, int offset, int limit,
        CancellationToken ct = default)
    {
        var query = _db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == conversationId);
        int total = await query.CountAsync(ct);
        var rows = await query.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).Skip(offset).Take(limit).ToListAsync(ct);
        return new(rows.Select(entity => string.IsNullOrWhiteSpace(entity.PayloadJson)
            ? new ChatMessage { Role = entity.Role, Content = entity.Content }
            : JsonSerializer.Deserialize<ChatMessage>(entity.PayloadJson)!).ToList(), total, offset);
    }

    public async Task SaveFactsAsync(ConversationEntity conversation, Dictionary<string, string> facts,
        CancellationToken ct = default, ChatResult? usage = null)
    {
        conversation.FactsJson = JsonSerializer.Serialize(facts);
        if (usage != null)
        {
            conversation.FactsPromptTokens += usage.PromptTokens;
            conversation.FactsCompletionTokens += usage.CompletionTokens;
            conversation.FactsUsageComplete &= usage.PromptTokens > 0 || usage.CompletionTokens > 0;
        }
        await _db.SaveChangesAsync(ct);
    }
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
        await SaveMessagesAsync(conversationId, [message], cancellationToken);
    }

    public async Task SaveMessagesAsync(
        int conversationId,
        IReadOnlyCollection<ChatMessage> messages,
        CancellationToken cancellationToken = default,
        PreparedContext? preparedContext = null)
    {
        if (messages.Count == 0)
        {
            return;
        }

        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
            await _db.Database.BeginTransactionAsync(cancellationToken);

        ConversationEntity? conversation = await _db.Conversations
            .FirstOrDefaultAsync(
                c => c.Id == conversationId,
                cancellationToken);

        if (conversation == null)
        {
            throw new InvalidOperationException(
                $"Диалог с ID {conversationId} не найден.");
        }

        List<ChatMessageEntity> entities = messages.Select(message => new ChatMessageEntity
        {
            ConversationId = conversationId,
            Role = message.Role,
            Content = message.Content,
            PayloadJson = JsonSerializer.Serialize(message),
            CreatedAt = DateTime.UtcNow
        }).ToList();

        _db.ChatMessages.AddRange(entities);

        // Заголовок формируем из первого сообщения пользователя
        ChatMessage? firstUserMessage = messages.FirstOrDefault(
            message => message.Role == "user");
        if (firstUserMessage != null && conversation.Title == "Новый диалог")
        {
            conversation.Title = firstUserMessage.Content.Length > 50
                ? firstUserMessage.Content[..50] + "..."
                : firstUserMessage.Content;
        }

        conversation.UpdatedAt = DateTime.UtcNow;

        if (preparedContext?.PendingFacts is { } facts)
        {
            conversation.FactsJson = JsonSerializer.Serialize(facts);
            if (preparedContext.SummaryCall is { } usage)
            {
                conversation.FactsPromptTokens += usage.PromptTokens;
                conversation.FactsCompletionTokens += usage.CompletionTokens;
                conversation.FactsUsageComplete &= usage.PromptTokens > 0 || usage.CompletionTokens > 0;
            }
        }

        if (preparedContext?.PendingProfile is { } profile)
        {
            conversation.UserProfileJson = JsonSerializer.Serialize(profile);
            if (preparedContext.ProfileCall is { } usage)
            {
                conversation.ProfilePromptTokens += usage.PromptTokens;
                conversation.ProfileCompletionTokens += usage.CompletionTokens;
                conversation.ProfileUsageComplete &= usage.PromptTokens > 0 || usage.CompletionTokens > 0;
            }
        }

        if (preparedContext?.PendingTaskState is { } taskState)
            conversation.TaskStateJson = JsonSerializer.Serialize(taskState);
        if (preparedContext?.ClearTaskState == true) conversation.TaskStateJson = "null";
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // Загрузка истории диалога
    public async Task<List<ChatMessage>> GetMessagesAsync(
        int conversationId,
        CancellationToken cancellationToken = default)
    {
        List<ChatMessageEntity> entities = await _db.ChatMessages
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .ToListAsync(cancellationToken);

        return entities.Select(entity =>
            string.IsNullOrWhiteSpace(entity.PayloadJson)
                ? new ChatMessage
                {
                    Role = entity.Role,
                    Content = entity.Content
                }
                : JsonSerializer.Deserialize<ChatMessage>(entity.PayloadJson)
                  ?? new ChatMessage
                  {
                      Role = entity.Role,
                      Content = entity.Content
                  })
            .ToList();
    }

    // Clear only the dialogue and derived summary. Explicit working/facts layers survive.
    public async Task ClearShortTermAsync(int conversationId, CancellationToken ct = default)
    {
        var messages = await _db.ChatMessages.Where(m => m.ConversationId == conversationId).ToListAsync(ct);
        _db.ChatMessages.RemoveRange(messages);
        var summary = await GetSummaryAsync(conversationId, ct);
        if (summary != null) _db.ContextSummaries.Remove(summary);
        await _db.SaveChangesAsync(ct);
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
        ContextSummaryEntity? summary = await GetSummaryAsync(conversationId, cancellationToken);
        if (summary != null)
        {
            _db.ContextSummaries.Remove(summary);
        }

        conversation.FactsJson = "{}";
        conversation.WorkingMemoryJson = "{}";
        conversation.UserProfileJson = "{}";
        conversation.TaskStateJson = "null";
        conversation.ProfilePromptTokens = conversation.ProfileCompletionTokens = 0;
        conversation.ProfileUsageComplete = true;
        conversation.FactsPromptTokens = conversation.FactsCompletionTokens = 0;
        conversation.FactsUsageComplete = true;
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

    public Task<ContextSummaryEntity?> GetSummaryAsync(
        int conversationId, CancellationToken cancellationToken = default) =>
        _db.ContextSummaries.FirstOrDefaultAsync(s => s.ConversationId == conversationId, cancellationToken);

    public async Task SaveSummaryAsync(
        int conversationId, string content, int coveredMessageCount,
        ChatResult usage, CancellationToken cancellationToken = default)
    {
        ContextSummaryEntity? summary = await GetSummaryAsync(conversationId, cancellationToken);
        if (summary == null)
        {
            summary = new ContextSummaryEntity { ConversationId = conversationId };
            _db.ContextSummaries.Add(summary);
        }

        summary.Content = content;
        summary.CoveredMessageCount = coveredMessageCount;
        summary.Revision++;
        summary.PromptTokens += usage.PromptTokens;
        summary.CompletionTokens += usage.CompletionTokens;
        summary.UsageComplete &= usage.PromptTokens > 0 || usage.CompletionTokens > 0;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetCompressionAsync(
        int conversationId, bool? enabled = null, int? keep = null, int? batch = null,
        CancellationToken cancellationToken = default)
    {
        if (keep is < 2 || batch is < 1)
        {
            throw new ArgumentException("Нужно keep >= 2 и batch >= 1.");
        }

        ConversationEntity conversation = await GetConversationAsync(conversationId)
            ?? throw new InvalidOperationException("Диалог не найден.");
        if (enabled.HasValue) conversation.CompressionEnabled = enabled.Value;
        if (batch.HasValue) conversation.ContextCompressionBatchSize = batch.Value;
        if (keep.HasValue && keep != conversation.ContextKeepLastMessages)
        {
            conversation.ContextKeepLastMessages = keep.Value;
            ContextSummaryEntity? summary = await GetSummaryAsync(conversationId, cancellationToken);
            if (summary != null)
            {
                // Rebuild with the new boundary, keeping the accumulated overhead.
                summary.Content = "";
                summary.CoveredMessageCount = 0;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
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
        if (temperature.HasValue && (!double.IsFinite(temperature.Value) || temperature is < 0 or > 2))
            throw new ArgumentException("Температура должна быть от 0 до 2.");
        if (maxTokens is <= 0 || reasoningBudgetTokens is <= 0) throw new ArgumentException("Лимит токенов должен быть положительным.");
        if (topP.HasValue && (!double.IsFinite(topP.Value) || topP is < 0 or > 1)) throw new ArgumentException("TopP от 0 до 1.");
        if (frequencyPenalty.HasValue && (!double.IsFinite(frequencyPenalty.Value) || frequencyPenalty is < -2 or > 2) ||
            presencePenalty.HasValue && (!double.IsFinite(presencePenalty.Value) || presencePenalty is < -2 or > 2))
            throw new ArgumentException("Штрафы должны быть от -2 до 2.");
        if (model != null && string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Модель пустая.");
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

        conversation.MaxTokens = null;
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

    public async Task SetAgentIdAsync(
        int conversationId,
        string agentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new ArgumentException("Не указан ID агента.", nameof(agentId));
        }

        ConversationEntity? conversation = await _db.Conversations
            .FirstOrDefaultAsync(
                c => c.Id == conversationId,
                cancellationToken);

        if (conversation == null)
        {
            throw new InvalidOperationException("Диалог не найден.");
        }

        conversation.AgentId = agentId;
        conversation.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
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
