using System.Diagnostics;
using UniversalAI.Core;
using UniversalAI.Core.Capabilities;
using UniversalAI.Providers;
using UniversalAI.Services.Agents;

namespace UniversalAI.Benchmarks;

/// <summary>Presentation of real provider calls; never supplies or replays model responses.</summary>
public sealed class Day9BenchmarkConsole(IAIProvider provider, bool automatic) : IAIProvider
{
    private int _callNumber;
    public string CallLabel { get; set; } = "ДИАЛОГ";
    public string Name => provider.Name;
    public ProviderCapabilities Capabilities => provider.Capabilities;

    public void Stage(int number, string title)
    {
        if (number > 1 && !automatic && !Console.IsInputRedirected)
        {
            Console.Write("\nНажмите Enter, чтобы перейти к следующему этапу... ");
            Console.ReadLine();
        }

        Console.WriteLine($"\n{new string('=', 72)}\nШАГ {number}. {title}\n{new string('=', 72)}");
    }

    public async Task<ChatResult> SendMessageAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        int number = ++_callNumber;
        Console.WriteLine($"\n[{DateTimeOffset.Now:HH:mm:ss}] HTTP/API #{number} — {CallLabel}");
        Console.WriteLine($"Модель: {request.Model}; сообщений в запросе: {request.Messages.Count}; " +
                          $"оценка входа: ~{TokenUsageCalculator.EstimateRequest(request)} токенов.");
        Console.WriteLine("Отправляем запрос провайдеру, ожидаем ответ...");
        Stopwatch watch = Stopwatch.StartNew();
        ChatResult response = await provider.SendMessageAsync(request, cancellationToken);
        watch.Stop();
        Console.WriteLine($"Ответ API за {watch.Elapsed.TotalSeconds:F2} с; finish={response.FinishReason}.");
        Console.WriteLine(response.Content);
        bool measured = response.PromptTokens > 0 || response.CompletionTokens > 0;
        Console.WriteLine(measured
            ? $"Токены API: вход {response.PromptTokens}, выход {response.CompletionTokens}, " +
              $"всего {response.PromptTokens + response.CompletionTokens}."
            : "Токены API: провайдер не вернул usage; расход не измерен.");
        return response;
    }

    public IAsyncEnumerable<string> StreamMessageAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
        provider.StreamMessageAsync(request, cancellationToken);
}
