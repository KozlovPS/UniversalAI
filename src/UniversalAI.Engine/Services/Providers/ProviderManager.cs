using Microsoft.EntityFrameworkCore;
using UniversalAI.Data;
using UniversalAI.Providers;

namespace UniversalAI.Services.Providers;

public class ProviderManager(AppDbContext db, IProviderFactory factory, ProviderCatalog catalog) : IProviderResolver
{
    public async Task<IAIProvider> GetProviderForConversationAsync(int conversationId,
        CancellationToken cancellationToken = default)
    {
        var conversation = await db.Conversations.Include(c => c.ProviderConnection)
            .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken)
            ?? throw new InvalidOperationException("Диалог не найден.");
        if (conversation.ProviderProfileId is { } profileId) return factory.Create(catalog.Get(profileId));
        var connection = conversation.ProviderConnection;
        if (connection == null) return factory.Create(catalog.Get(catalog.DefaultId));
        return factory.Create(new ProviderProfile
        {
            Id = $"connection-{connection.Id}", Name = connection.Name, Type = connection.ProviderType,
            BaseUrl = connection.BaseUrl, DefaultModel = connection.DefaultModel,
            ApiKeyEnvironmentVariable = connection.ApiKeyEnvironmentVariable
        });
    }
}
