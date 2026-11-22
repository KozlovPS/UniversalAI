using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UniversalAI.Data;
using UniversalAI.Services.Agents;
using UniversalAI.Services.Providers;

namespace UniversalAI.Services;

/// <summary>Shared use cases. Each call owns a scope; one conversation executes sequentially.</summary>
public sealed class WorkspaceService(IServiceScopeFactory scopes, ConversationGate gate,
    ProviderCatalog profiles) : IWorkspaceClient
{
    private async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    private static async Task<ConversationInfo> InfoAsync(ConversationService service, int id)
    {
        var c = await service.GetConversationAsync(id) ?? throw new KeyNotFoundException("Диалог не найден.");
        var summary = await service.GetSummaryAsync(id);
        return new(c.Id, c.Title, c.Model, c.AgentId, c.ProviderProfileId, c.MemoryStrategy,
            c.CompressionEnabled, c.ContextKeepLastMessages, c.ContextCompressionBatchSize,
            c.Temperature, c.MaxTokens, c.SystemPrompt, c.UpdatedAt, c.FactsJson, summary?.Content,
            c.TopP, c.FrequencyPenalty, c.PresencePenalty, c.Seed, c.ReasoningEffort, c.ReasoningBudgetTokens, c.WorkingMemoryJson, c.UserProfileJson);
    }

    public Task<IReadOnlyList<ProviderInfo>> ProvidersAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ProviderInfo>>(
        profiles.Profiles.Select(p => new ProviderInfo(p.Id, p.Name, p.Type, p.BaseUrl, p.DefaultModel, p.SupportsTools,
            !p.RequireApiKey || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(p.ApiKeyEnvironmentVariable ?? "")))).ToList());

    public Task<IReadOnlyList<AgentInfo>> AgentsAsync(CancellationToken ct = default) => InScopeAsync(sp =>
        Task.FromResult<IReadOnlyList<AgentInfo>>(sp.GetRequiredService<AgentRegistry>().GetAvailableAgents()
            .Select(a => new AgentInfo(a.Id, a.DisplayName, a.Description)).ToList()));

    public Task<IReadOnlyList<string>> ModelsAsync(int id, CancellationToken ct = default) => InScopeAsync(async sp =>
    {
        var provider = await sp.GetRequiredService<ProviderManager>().GetProviderForConversationAsync(id, ct);
        if (provider is UniversalAI.Providers.OpenAICompatible.OpenAICompatibleProvider compatible)
            return (IReadOnlyList<string>)(await compatible.GetModelsAsync(ct)).Select(m => m.Id).ToList();
        return [(await InfoAsync(sp.GetRequiredService<ConversationService>(), id)).Model];
    });

    public Task<TokenStatistics> TokensAsync(int id, CancellationToken ct = default) => InScopeAsync(async sp =>
    {
        var service = sp.GetRequiredService<ConversationService>();
        _ = await InfoAsync(service, id);
        var summary = await service.GetSummaryAsync(id, ct);
        var conversation = (await service.GetConversationAsync(id))!;
        return new TokenStatistics(TokenUsageCalculator.SummarizeHistory(await service.GetMessagesAsync(id, ct)),
            (summary?.PromptTokens ?? 0) + conversation.FactsPromptTokens + conversation.ProfilePromptTokens,
            (summary?.CompletionTokens ?? 0) + conversation.FactsCompletionTokens + conversation.ProfileCompletionTokens,
            (summary?.UsageComplete ?? true) && conversation.FactsUsageComplete && conversation.ProfileUsageComplete);
    });

    public Task<IReadOnlyList<ConversationInfo>> ListAsync(CancellationToken ct = default) => InScopeAsync(async sp =>
    {
        var service = sp.GetRequiredService<ConversationService>();
        var result = new List<ConversationInfo>();
        foreach (var row in await service.GetConversationsAsync(ct)) result.Add(await InfoAsync(service, row.Id));
        return (IReadOnlyList<ConversationInfo>)result;
    });
    public Task<ConversationInfo> GetAsync(int id, CancellationToken ct = default) =>
        InScopeAsync(sp => InfoAsync(sp.GetRequiredService<ConversationService>(), id));

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        using var lease = await gate.EnterAsync(id, ct);
        await InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var conversation = await db.Conversations.SingleOrDefaultAsync(c => c.Id == id, ct)
                ?? throw new KeyNotFoundException("Диалог не найден.");
            // Messages and summary belong to the conversation and cascade in the same transaction.
            db.Conversations.Remove(conversation);
            await db.SaveChangesAsync(ct);
            return true;
        });
    }

    public Task<ConversationInfo> CreateAsync(CreateConversationRequest request, CancellationToken ct = default) => InScopeAsync(async sp =>
    {
        var profile = profiles.Get(request.ProviderId ?? profiles.DefaultId);
        _ = sp.GetRequiredService<AgentRegistry>().Get(request.AgentId);
        var service = sp.GetRequiredService<ConversationService>();
        int id = await service.CreateConversationAsync(ct);
        var c = (await service.GetConversationAsync(id))!;
        c.ProviderProfileId = profile.Id; c.Model = request.Model ?? profile.DefaultModel; c.AgentId = request.AgentId;
        if (!string.IsNullOrWhiteSpace(request.Title)) c.Title = request.Title.Trim();
        await sp.GetRequiredService<AppDbContext>().SaveChangesAsync(ct);
        return await InfoAsync(service, id);
    });

    public Task<MessagePage> MessagesAsync(int id, int offset = 0, int limit = 100, CancellationToken ct = default) => InScopeAsync(async sp =>
    {
        if (offset < 0 || limit is < 1 or > 500) throw new ArgumentException("offset >= 0; limit от 1 до 500.");
        var service = sp.GetRequiredService<ConversationService>();
        if (!await service.ConversationExistsAsync(id, ct)) throw new KeyNotFoundException("Диалог не найден.");
        return await service.GetMessagePageAsync(id, offset, limit, ct);
    });

    public async Task<ConversationInfo> UpdateAsync(int id, ConversationSettings settings, CancellationToken ct = default)
    {
        using var lease = await gate.EnterAsync(id, ct);
        return await InScopeAsync(async sp =>
        {
            var service = sp.GetRequiredService<ConversationService>();
            var c = await service.GetConversationAsync(id) ?? throw new KeyNotFoundException("Диалог не найден.");
            ValidateSettings(settings);
            if (settings.AgentId != null) { _ = sp.GetRequiredService<AgentRegistry>().Get(settings.AgentId); c.AgentId = settings.AgentId; }
            if (settings.ProviderId != null)
            {
                var profile = profiles.Get(settings.ProviderId);
                c.ProviderProfileId = profile.Id; c.ProviderConnectionId = null; c.Model = profile.DefaultModel;
                if (!profile.SupportsTools && settings.AgentId == null) c.AgentId = "direct";
            }
            if (settings.MemoryStrategy != null) c.MemoryStrategy = settings.MemoryStrategy;
            await using var transaction = await sp.GetRequiredService<AppDbContext>().Database.BeginTransactionAsync(ct);
            if (settings.WorkingMemoryJson != null) c.WorkingMemoryJson = settings.WorkingMemoryJson;
            if (settings.LongTermMemoryJson != null) c.FactsJson = settings.LongTermMemoryJson;
            if (settings.UserProfileJson != null) c.UserProfileJson = settings.UserProfileJson;
            if (settings.ClearMaxTokens) c.MaxTokens = null;
            if (settings.ClearShortTerm) await service.ClearShortTermAsync(id, ct);
            if (settings.ResetGeneration) await service.ResetGenerationSettingsAsync(id);
            if (settings.ClearReasoning) { c.ReasoningEffort = null; c.ReasoningBudgetTokens = null; }
            await service.UpdateSettingsAsync(id, systemPrompt: settings.SystemPrompt, model: settings.Model,
                temperature: settings.Temperature, maxTokens: settings.MaxTokens, topP: settings.TopP,
                frequencyPenalty: settings.FrequencyPenalty, presencePenalty: settings.PresencePenalty,
                seed: settings.Seed, reasoningEffort: settings.ReasoningEffort, reasoningBudgetTokens: settings.ReasoningBudgetTokens);
            await service.SetCompressionAsync(id, settings.CompressionEnabled, settings.Keep, settings.Batch, ct);
            if (settings.Title != null) await service.RenameConversationAsync(id, settings.Title);
            await sp.GetRequiredService<AppDbContext>().SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return await InfoAsync(service, id);
        });
    }

    private static void ValidateSettings(ConversationSettings s)
    {
        if (s.MemoryStrategy != null && s.MemoryStrategy is not ("Summary" or "SlidingWindow" or "StickyFacts" or "Layered"))
            throw new ArgumentException("Стратегия: Summary, SlidingWindow, StickyFacts или Layered.");
        if (s.WorkingMemoryJson != null) _ = Context.LayeredMemory.Parse(s.WorkingMemoryJson);
        if (s.LongTermMemoryJson != null) _ = Context.LayeredMemory.Parse(s.LongTermMemoryJson);
        if (s.UserProfileJson != null) _ = Context.UserProfileMemory.Parse(s.UserProfileJson);
        if (s.Keep is < 2 || s.Batch is < 1 || s.MaxTokens is < 1 ||
            s.Temperature is < 0 or > 2 || s.Temperature.HasValue && !double.IsFinite(s.Temperature.Value))
            throw new ArgumentException("Проверьте параметры генерации и памяти.");
        if (s.Model != null && string.IsNullOrWhiteSpace(s.Model)) throw new ArgumentException("Модель пустая.");
        if (s.SystemPrompt != null && string.IsNullOrWhiteSpace(s.SystemPrompt)) throw new ArgumentException("Промпт пустой.");
        if (s.Title != null && string.IsNullOrWhiteSpace(s.Title)) throw new ArgumentException("Название пустое.");
    }

    public async Task ResetAsync(int id, CancellationToken ct = default)
    {
        using var lease = await gate.EnterAsync(id, ct);
        await InScopeAsync(async sp => { await sp.GetRequiredService<ConversationService>().ClearConversationAsync(id, ct); return true; });
    }

    public async Task<AgentTurnResult> SendAsync(int id, string content, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(content)) throw new ArgumentException("Сообщение пустое.");
        using var lease = await gate.EnterAsync(id, ct);
        return await InScopeAsync(async sp =>
        {
            var c = await sp.GetRequiredService<ConversationService>().GetConversationAsync(id)
                ?? throw new KeyNotFoundException("Диалог не найден.");
            return await sp.GetRequiredService<AgentRegistry>().Get(c.AgentId).RunAsync(id, content, ct);
        });
    }

    public async Task<ConversationInfo> CompressAsync(int id, CancellationToken ct = default)
    {
        using var lease = await gate.EnterAsync(id, ct);
        return await InScopeAsync(async sp =>
        {
            var service = sp.GetRequiredService<ConversationService>();
            var c = await service.GetConversationAsync(id) ?? throw new KeyNotFoundException("Диалог не найден.");
            if (!c.CompressionEnabled || c.MemoryStrategy != "Summary") throw new ArgumentException("Включите стратегию Summary.");
            var provider = await sp.GetRequiredService<ProviderManager>().GetProviderForConversationAsync(id, ct);
            await sp.GetRequiredService<ContextCompressionService>().PrepareAsync(c, await service.GetMessagesAsync(id, ct), provider, ct, force: true);
            return await InfoAsync(service, id);
        });
    }

    public async Task<ConversationInfo> BranchAsync(int id, string title, CancellationToken ct = default)
    {
        using var lease = await gate.EnterAsync(id, ct);
        return await InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var service = sp.GetRequiredService<ConversationService>();
            var original = await db.Conversations.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct)
                ?? throw new KeyNotFoundException("Диалог не найден.");
            original.Id = 0; original.Title = string.IsNullOrWhiteSpace(title) ? original.Title + " (ветка)" : title.Trim();
            original.CreatedAt = original.UpdatedAt = DateTime.UtcNow;
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            db.Conversations.Add(original); await db.SaveChangesAsync(ct);
            var messages = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == id).ToListAsync(ct);
            foreach (var message in messages) { message.Id = 0; message.ConversationId = original.Id; message.Conversation = null!; }
            db.ChatMessages.AddRange(messages);
            var summary = await db.ContextSummaries.AsNoTracking().SingleOrDefaultAsync(s => s.ConversationId == id, ct);
            if (summary != null) { summary.ConversationId = original.Id; db.ContextSummaries.Add(summary); }
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            return await InfoAsync(service, original.Id);
        });
    }
}
