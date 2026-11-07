
using Microsoft.EntityFrameworkCore;
using UniversalAI.Data;
using UniversalAI.Providers;
using UniversalAI.Providers.GigaChat;
using UniversalAI.Providers.OpenAICompatible;

namespace UniversalAI.Services.Providers;

public class ProviderManager
{
    private readonly AppDbContext _db;
    private readonly HttpClient _http;
    private readonly GigaChatOptions _gigaChatOptions;

    public ProviderManager(
        AppDbContext db,
        HttpClient http,
        GigaChatOptions gigaChatOptions)
    {
        _db = db;
        _http = http;
        _gigaChatOptions = gigaChatOptions;
    }

    public async Task<IAIProvider> GetProviderForConversationAsync(
        int conversationId,
        CancellationToken cancellationToken = default)
    {
        Data.Entities.ConversationEntity? conversation = await _db.Conversations
            .Include(c => c.ProviderConnection)
            .FirstOrDefaultAsync(
                c => c.Id == conversationId,
                cancellationToken);

        if (conversation == null)
        {
            throw new InvalidOperationException(
                "Диалог не найден.");
        }

        Data.Entities.ProviderConnectionEntity? connection = conversation.ProviderConnection;

        // Если подключение не выбрано,
        // используем GigaChat по умолчанию.
        if (connection == null)
        {
            return new GigaChatProvider(
                _http,
                _gigaChatOptions);
        }

        if (connection.ProviderType == "OpenAICompatible")
        {
            OpenAICompatibleOptions options = new()
            {
                Name = connection.Name,
                BaseUrl = connection.BaseUrl,
                ApiKeyEnvironmentVariable = connection.ApiKeyEnvironmentVariable,
                DefaultModel = connection.DefaultModel
            };

            return new OpenAICompatibleProvider(
                _http,
                options);
        }

        return connection.ProviderType == "GigaChat"
            ? (IAIProvider)new GigaChatProvider(
                _http,
                _gigaChatOptions)
            : throw new NotSupportedException(
            $"Неизвестный тип провайдера: {connection.ProviderType}");
    }
}
