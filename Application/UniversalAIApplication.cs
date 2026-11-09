using Microsoft.EntityFrameworkCore;
using UniversalAI.Benchmarks;
using UniversalAI.ConsoleApp;
using UniversalAI.ConsoleApp.Commands;
using UniversalAI.Data;
using UniversalAI.Providers.GigaChat;
using UniversalAI.Providers.OpenAICompatible;
using UniversalAI.Services;
using UniversalAI.Services.Agents;
using UniversalAI.Services.Providers;

namespace UniversalAI.Application;

/// <summary>Application composition root: selects the run mode and wires dependencies.</summary>
public static class UniversalAIApplication
{
    public static async Task<int> RunAsync(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (TryGetBenchmarkArguments(args, out string[] benchmarkArguments))
        {
            return await BenchmarkCommand.RunAsync(benchmarkArguments);
        }

        GigaChatOptions gigaOptions = GigaChatOptions.FromEnvironment();
        OpenAICompatibleOptions proxyOptions = OpenAICompatibleOptions.FromEnvironment();

        DbContextOptions<AppDbContext> dbOptions =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite("Data Source=universalai.db")
                .Options;

        await using AppDbContext db = new(dbOptions);
        await DatabaseInitializer.InitializeAsync(db);
        using HttpClient http = new();

        ConversationService conversations = new(db);
        ProviderConnectionService providerConnections = new(db);
        ProviderManager providers = new(db, http, gigaOptions);
        ToolCallingAgent toolCallingAgent = new(
            conversations,
            providers,
            new AgentToolRegistry());
        ChatService chat = new(conversations, providers);
        AgentRegistry agents = new(
        [
            toolCallingAgent,
            new DirectChatAgent(chat)
        ]);

        ConsoleCommandRouter commands = new(
        [
            new ProviderCommandHandler(
                db,
                conversations,
                providerConnections,
                providers,
                gigaOptions,
                proxyOptions),
            new HelpCommandHandler(),
            new AgentCommandHandler(agents, conversations),
            new ConversationCommandHandler(conversations),
            new GenerationSettingsCommandHandler(conversations, providers)
        ]);

        ConsoleChatApplication console = new(conversations, providers, agents, commands);
        await console.RunAsync();
        return 0;
    }

    private static bool TryGetBenchmarkArguments(
        string[] args,
        out string[] benchmarkArguments)
    {
        benchmarkArguments = [];
        if (args.Length == 0 ||
            !args[0].StartsWith("--benchmark", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (args[0].Equals("--benchmark", StringComparison.OrdinalIgnoreCase))
        {
            benchmarkArguments = args[1..];
        }
        else if (args[0].StartsWith("--benchmark-day", StringComparison.OrdinalIgnoreCase))
        {
            benchmarkArguments = [args[0]["--benchmark-".Length..], .. args[1..]];
        }
        else
        {
            benchmarkArguments = args;
        }

        return true;
    }
}
