using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UniversalAI.Benchmarks;
using UniversalAI.Benchmarks.Runtime;
using UniversalAI.Configuration;
using UniversalAI.ConsoleApp;
using UniversalAI.ConsoleApp.Commands;
using UniversalAI.Services;

namespace UniversalAI.Application;

public static class UniversalAIApplication
{
    public static async Task<int> RunAsync(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            if (args.FirstOrDefault() is "--help" or "-h")
            {
                Console.WriteLine("UniversalAI: без аргументов — локальный чат; --server URL — клиент сервера; --benchmark dayN [параметры]; --runs — список прогонов.\nКаталог данных: UNIVERSALAI_DATA_DIR. Токен сервера: UNIVERSALAI_SERVER_TOKEN.");
                return 0;
            }
            if (args.FirstOrDefault() == "--server")
            {
                if (args.Length < 2) throw new ArgumentException("Укажите --server http://localhost:5080/");
                using var http = new HttpClient { BaseAddress = new Uri(args[1].TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(20) };
                string? token = Environment.GetEnvironmentVariable("UNIVERSALAI_SERVER_TOKEN");
                if (!string.IsNullOrWhiteSpace(token)) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return await RemoteConsole.RunAsync(http, args[2..], cancellation.Token);
            }

            // Legacy help, rescore and offline checks remain available without opening the working DB.
            string[]? benchmarkArgs = BenchmarkArguments(args);
            if (benchmarkArgs != null && benchmarkArgs.Any(a => a is "help" or "--help" or "-h" or "--self-test" or "--rescore"))
                return await BenchmarkCommand.RunAsync(benchmarkArgs);

            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
                .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.local.json"), optional: true)
                .AddEnvironmentVariables("UNIVERSALAI_");
            builder.Services.AddUniversalAI(builder.Configuration).AddUniversalAIBenchmarks();
            using var host = builder.Build();
            var paths = host.Services.GetRequiredService<AppPaths>();
            using var lease = new StorageLease(paths);
            await host.Services.GetRequiredService<FileRunStore>().RecoverInterruptedAsync(cancellation.Token);
            Console.WriteLine($"Данные: {paths.Root}");
            if (args.FirstOrDefault() == "--runs")
            {
                foreach (var run in await host.Services.GetRequiredService<FileRunStore>().ListAsync(cancellation.Token))
                    Console.WriteLine($"{run.Id} {run.BenchmarkId} {run.Status} {run.Model} {run.CompletedCalls} вызовов");
                return 0;
            }
            if (benchmarkArgs != null)
                return await BenchmarkConsoleRunner.RunAsync(host.Services, benchmarkArgs, cancellation.Token);
            if (args.Length > 0) throw new ArgumentException("Неизвестные аргументы. Справка: --help");
            await host.Services.InitializeUniversalAIAsync(cancellation.Token);
            var workspace = host.Services.GetRequiredService<IWorkspaceClient>();
            var router = new ConsoleCommandRouter([new HelpCommandHandler(), new WorkspaceCommandHandler(workspace)]);
            await new ConsoleChatApplication(workspace, router).RunAsync(cancellation.Token);
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { Console.WriteLine("Выполнение отменено."); return 130; }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
        finally { Console.CancelKeyPress -= cancelHandler; }
    }

    private static string[]? BenchmarkArguments(string[] args)
    {
        if (args.Length == 0) return null;
        if (args[0] == "--benchmark") return args[1..];
        return args[0].StartsWith("--benchmark-day", StringComparison.OrdinalIgnoreCase)
            ? [args[0]["--benchmark-".Length..], .. args[1..]] : null;
    }
}
