using Microsoft.EntityFrameworkCore;
using UniversalAI.Data;
using UniversalAI.Data.Entities;
using UniversalAI.Providers;
using UniversalAI.Providers.GigaChat;
using UniversalAI.Providers.OpenAICompatible;
using UniversalAI.Services;
using UniversalAI.Services.Providers;

namespace UniversalAI.ConsoleApp.Commands;

public sealed class ProviderCommandHandler : IConsoleCommandHandler
{
    private readonly AppDbContext _db;
    private readonly ConversationService _conversations;
    private readonly ProviderConnectionService _connections;
    private readonly ProviderManager _providers;
    private readonly GigaChatOptions _gigaOptions;
    private readonly OpenAICompatibleOptions _proxyOptions;

    public ProviderCommandHandler(
        AppDbContext db,
        ConversationService conversations,
        ProviderConnectionService connections,
        ProviderManager providers,
        GigaChatOptions gigaOptions,
        OpenAICompatibleOptions proxyOptions)
    {
        _db = db;
        _conversations = conversations;
        _connections = connections;
        _providers = providers;
        _gigaOptions = gigaOptions;
        _proxyOptions = proxyOptions;
    }

    public async Task<bool> TryHandleAsync(
        string input,
        ConsoleChatSession session,
        CancellationToken cancellationToken = default)
    {
        if (input.Equals("/provider", StringComparison.OrdinalIgnoreCase))
        {
            ConversationEntity? conversation =
                await _conversations.GetConversationAsync(session.ConversationId);
            IAIProvider provider =
                await _providers.GetProviderForConversationAsync(session.ConversationId);

            Console.WriteLine($"Провайдер: {provider.Name}");
            Console.WriteLine($"Модель: {conversation?.Model}");
            Console.WriteLine("Доступно: /provider GigaChat, /provider ProxyAPI");
            return true;
        }

        if (input.StartsWith("/provider ", StringComparison.OrdinalIgnoreCase))
        {
            await SelectProviderAsync(input["/provider ".Length..].Trim(), session.ConversationId);
            return true;
        }

        if (input.Equals("/models", StringComparison.OrdinalIgnoreCase))
        {
            await ShowModelsAsync(session.ConversationId);
            return true;
        }

        return false;
    }

    private async Task SelectProviderAsync(string providerName, int conversationId)
    {
        try
        {
            if (providerName.Equals("GigaChat", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(_gigaOptions.AuthKey))
                {
                    Console.WriteLine("Не найдена переменная окружения GIGACHAT_AUTH_KEY.");
                    return;
                }

                await _conversations.SetProviderConnectionAsync(conversationId, null);
                await _conversations.UpdateSettingsAsync(
                    conversationId,
                    model: _gigaOptions.DefaultModel);
                await _conversations.ResetGenerationSettingsAsync(conversationId);

                Console.WriteLine("Провайдер: GigaChat");
                Console.WriteLine($"Модель: {_gigaOptions.DefaultModel}");
                return;
            }

            if (providerName.Equals("ProxyAPI", StringComparison.OrdinalIgnoreCase))
            {
                ProviderConnectionEntity? connection = await GetOrCreateProxyConnectionAsync();
                if (connection == null)
                {
                    return;
                }

                await _conversations.SetProviderConnectionAsync(conversationId, connection.Id);
                await _conversations.UpdateSettingsAsync(
                    conversationId,
                    model: _proxyOptions.DefaultModel);
                await _conversations.ResetGenerationSettingsAsync(conversationId);

                Console.WriteLine("Провайдер: ProxyAPI");
                Console.WriteLine($"Модель: {_proxyOptions.DefaultModel}");
                return;
            }

            Console.WriteLine("Неизвестный провайдер. Используй /provider GigaChat или /provider ProxyAPI.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка переключения провайдера: {ex.Message}");
        }
    }

    private async Task<ProviderConnectionEntity?> GetOrCreateProxyConnectionAsync()
    {
        string variableName = _proxyOptions.ApiKeyEnvironmentVariable;
        if (string.IsNullOrWhiteSpace(variableName) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variableName)))
        {
            Console.WriteLine($"Не найдена переменная окружения {variableName}.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(_proxyOptions.DefaultModel))
        {
            Console.WriteLine("В настройках ProxyAPI не задана модель по умолчанию.");
            return null;
        }

        List<ProviderConnectionEntity> connections =
            await _connections.GetConnectionsAsync();

        ProviderConnectionEntity? connection = connections.FirstOrDefault(item =>
            item.ProviderType == "OpenAICompatible" &&
            item.Name.Equals(_proxyOptions.Name, StringComparison.OrdinalIgnoreCase));

        if (connection == null)
        {
            int id = await _connections.AddConnectionAsync(
                _proxyOptions.Name,
                "OpenAICompatible",
                _proxyOptions.BaseUrl,
                variableName,
                _proxyOptions.DefaultModel);
            return await _connections.GetConnectionAsync(id);
        }

        connection.BaseUrl = _proxyOptions.BaseUrl.TrimEnd('/');
        connection.ApiKeyEnvironmentVariable = variableName;
        connection.DefaultModel = _proxyOptions.DefaultModel;
        await _db.SaveChangesAsync();
        return connection;
    }

    private async Task ShowModelsAsync(int conversationId)
    {
        try
        {
            IAIProvider provider =
                await _providers.GetProviderForConversationAsync(conversationId);

            if (provider is not OpenAICompatibleProvider compatibleProvider)
            {
                Console.WriteLine(
                    $"Получение списка моделей пока не реализовано для {provider.Name}.");
                return;
            }

            List<OpenAICompatibleModel> models = await compatibleProvider.GetModelsAsync();
            Console.WriteLine($"\nДоступные модели {provider.Name}:");
            foreach (OpenAICompatibleModel model in models)
            {
                Console.WriteLine($"  {model.Id}");
            }

            Console.WriteLine($"\nВсего моделей: {models.Count}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка: {ex.Message}");
        }
    }
}
