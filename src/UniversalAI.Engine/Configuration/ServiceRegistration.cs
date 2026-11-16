using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UniversalAI.Data;
using UniversalAI.Services;
using UniversalAI.Services.Agents;
using UniversalAI.Services.Providers;

namespace UniversalAI.Configuration;

public static class ServiceRegistration
{
    public static IServiceCollection AddUniversalAI(this IServiceCollection services, IConfiguration configuration)
    {
        var paths = new AppPaths(configuration);
        paths.EnsureCreated();
        services.AddSingleton(paths);
        services.AddSingleton(new ProviderCatalog(configuration));
        services.AddHttpClient("ai", client => client.Timeout = TimeSpan.FromSeconds(configuration.GetValue("Providers:TimeoutSeconds", 180)));
        services.AddSingleton<IProviderFactory, ProviderFactory>();
        services.AddDbContextFactory<AppDbContext>(options => options.UseSqlite(new SqliteConnectionStringBuilder
        {
            DataSource = paths.Database, ForeignKeys = true, DefaultTimeout = 30
        }.ToString()));
        services.AddScoped<ConversationService>();
        services.AddScoped<IContextMemoryStore>(sp => sp.GetRequiredService<ConversationService>());
        services.AddScoped<ProviderConnectionService>();
        services.AddScoped<ProviderManager>();
        services.AddScoped<IProviderResolver>(sp => sp.GetRequiredService<ProviderManager>());
        services.AddScoped<ContextCompressionService>();
        services.AddSingleton<AgentToolRegistry>();
        services.AddScoped<IAgent, ToolCallingAgent>();
        services.AddScoped<IAgent, DirectChatAgent>();
        services.AddScoped<ChatService>();
        services.AddScoped<AgentRegistry>();
        services.AddSingleton<ConversationGate>();
        services.AddSingleton<WorkspaceService>();
        services.AddSingleton<IWorkspaceClient>(sp => sp.GetRequiredService<WorkspaceService>());
        return services;
    }

    public static async Task InitializeUniversalAIAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await DatabaseInitializer.InitializeAsync(db, ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
    }
}
