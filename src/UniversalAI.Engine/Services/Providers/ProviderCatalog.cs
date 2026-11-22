using Microsoft.Extensions.Configuration;
using UniversalAI.Core.Capabilities;
using UniversalAI.Providers;
using UniversalAI.Providers.GigaChat;
using UniversalAI.Providers.OpenAICompatible;

namespace UniversalAI.Services.Providers;

public sealed class ProviderProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "OpenAICompatible";
    public string BaseUrl { get; set; } = "";
    public string DefaultModel { get; set; } = "";
    public string? ApiKeyEnvironmentVariable { get; set; }
    public bool RequireApiKey { get; set; } = true;
    public bool SupportsTools { get; set; } = true;
    public ModelCapabilities? ModelCapabilities { get; set; }
}

/// <summary>Profiles describe cloud or local endpoints without storing API keys.</summary>
public sealed class ProviderCatalog
{
    public IReadOnlyList<ProviderProfile> Profiles { get; }
    public string DefaultId { get; }

    public ProviderCatalog(IConfiguration configuration)
    {
        Profiles = configuration.GetSection("Providers:Profiles").Get<List<ProviderProfile>>() ?? [];
        if (Profiles.Count == 0) throw new InvalidOperationException("Настройте Providers:Profiles.");
        if (Profiles.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Profiles.Count)
            throw new InvalidOperationException("ID профилей провайдеров должны быть уникальны.");
        foreach (var profile in Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.DefaultModel))
                throw new InvalidOperationException("Профиль требует Id и DefaultModel.");
            ValidateEndpoint(profile.BaseUrl);
        }
        DefaultId = configuration["Providers:Default"] ?? Profiles[0].Id;
        _ = Get(DefaultId);
    }

    public ProviderProfile Get(string id) => Profiles.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Профиль '{id}' не найден.");

    public static void ValidateEndpoint(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Base URL должен быть абсолютным HTTP(S) адресом без учётных данных и query.");
    }
}

public interface IProviderFactory
{
    IAIProvider Create(ProviderProfile profile);
}

public sealed class ProviderFactory(IHttpClientFactory clients) : IProviderFactory
{
    // GigaChatProvider owns a synchronized OAuth cache, so reuse it across operations.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IAIProvider> _giga = new();

    public IAIProvider Create(ProviderProfile profile)
    {
        if (profile.Type == "GigaChat")
            return _giga.GetOrAdd(profile.Id, _ => new GigaChatProvider(clients.CreateClient("ai"), new GigaChatOptions
            {
                AuthKey = Environment.GetEnvironmentVariable(profile.ApiKeyEnvironmentVariable ?? "GIGACHAT_AUTH_KEY") ?? "",
                BaseUrl = profile.BaseUrl, DefaultModel = profile.DefaultModel
            }));
        if (profile.Type != "OpenAICompatible") throw new NotSupportedException($"Тип провайдера {profile.Type} не зарегистрирован.");
        return new OpenAICompatibleProvider(clients.CreateClient("ai"), new OpenAICompatibleOptions
        {
            Name = profile.Name, BaseUrl = profile.BaseUrl, DefaultModel = profile.DefaultModel,
            ApiKeyEnvironmentVariable = profile.ApiKeyEnvironmentVariable ?? "",
            RequireApiKey = profile.RequireApiKey, SupportsTools = profile.SupportsTools,
            ModelCapabilities = profile.ModelCapabilities
        });
    }
}
