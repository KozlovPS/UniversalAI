using System.Net.Http.Json;
using System.Text.Json;
using UniversalAI.Benchmarks.Runtime;
using UniversalAI.ConsoleApp;
using UniversalAI.ConsoleApp.Commands;
using UniversalAI.Services;

namespace UniversalAI.Application;

public static class RemoteConsole
{
    public static async Task<int> RunAsync(HttpClient http, string[] args, CancellationToken ct)
    {
        if (args.FirstOrDefault() == "--runs")
        {
            using var response = await http.GetAsync("api/runs", ct);
            response.EnsureSuccessStatusCode(); Console.WriteLine(await response.Content.ReadAsStringAsync(ct)); return 0;
        }
        if (args.FirstOrDefault() == "--cancel" && args.Length == 2)
        {
            using var response = await http.PostAsJsonAsync($"api/runs/{Uri.EscapeDataString(args[1])}/cancel", new { }, ct);
            response.EnsureSuccessStatusCode(); Console.WriteLine("Отмена запрошена."); return 0;
        }
        if (args.FirstOrDefault() == "--benchmark")
        {
            var parsed = BenchmarkConsoleRunner.Parse(args[1..]);
            if (parsed.Output != null || parsed.BaseUrl != null || parsed.ApiKeyEnvironmentVariable != null || parsed.Settings.TranscriptPath != null)
                throw new ArgumentException("Удалённый запуск использует серверные профили и каталог результатов. Уберите --output/--base-url/--api-key-env/--transcript.");
            using var response = await http.PostAsJsonAsync("api/runs", new { benchmarkId = parsed.BenchmarkId, settings = parsed.Settings }, ct);
            response.EnsureSuccessStatusCode();
            var run = await response.Content.ReadFromJsonAsync<BenchmarkRun>(FileRunStore.Json, ct) ?? throw new InvalidDataException();
            Console.WriteLine($"RunId: {run.Id}. Выполняется на сервере; --runs показывает состояние, --cancel ID отменяет."); return 0;
        }
        if (args.Length > 0) throw new ArgumentException("Неизвестные аргументы удалённой консоли.");
        IWorkspaceClient workspace = new RemoteWorkspaceClient(http);
        await new ConsoleChatApplication(workspace, new ConsoleCommandRouter([new HelpCommandHandler(), new WorkspaceCommandHandler(workspace)])).RunAsync(ct);
        return 0;
    }
}
