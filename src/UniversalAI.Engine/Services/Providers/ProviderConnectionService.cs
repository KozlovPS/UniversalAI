
using Microsoft.EntityFrameworkCore;
using UniversalAI.Data;
using UniversalAI.Data.Entities;

namespace UniversalAI.Services.Providers;

public class ProviderConnectionService
{
    private readonly AppDbContext _db;

    public ProviderConnectionService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<int> AddConnectionAsync(
        string name,
        string providerType,
        string baseUrl,
        string apiKeyEnvironmentVariable,
        string defaultModel)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Укажи название подключения.");
        }


        if (string.IsNullOrWhiteSpace(apiKeyEnvironmentVariable))
        {
            throw new ArgumentException(
                "Укажи имя переменной окружения API-ключа.");
        }

        if (string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(apiKeyEnvironmentVariable)))
        {
            throw new InvalidOperationException(
                $"Не найдена переменная окружения " +
                $"'{apiKeyEnvironmentVariable}' или она пуста.");
        }


        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException("Укажи Base URL.");
        }

        ProviderConnectionEntity connection = new()
        {
            Name = name,
            ProviderType = providerType,
            BaseUrl = baseUrl.TrimEnd('/'),
            ApiKeyEnvironmentVariable = apiKeyEnvironmentVariable,
            DefaultModel = defaultModel,
            CreatedAt = DateTime.UtcNow
        };

        _db.ProviderConnections.Add(connection);

        await _db.SaveChangesAsync();

        return connection.Id;
    }

    public async Task<List<ProviderConnectionEntity>> GetConnectionsAsync()
    {
        return await _db.ProviderConnections
            .OrderBy(c => c.Name)
            .ToListAsync();
    }

    public async Task<ProviderConnectionEntity?> GetConnectionAsync(int id)
    {
        return await _db.ProviderConnections
            .FirstOrDefaultAsync(c => c.Id == id);
    }

    public async Task DeleteConnectionAsync(int id)
    {
        ProviderConnectionEntity? connection =
            await GetConnectionAsync(id);

        if (connection == null)
        {
            throw new InvalidOperationException(
                "Подключение не найдено.");
        }

        _db.ProviderConnections.Remove(connection);

        await _db.SaveChangesAsync();
    }
}
