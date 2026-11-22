using UniversalAI.Benchmarks.Runtime;
using UniversalAI.Providers;
using System.Diagnostics;
using UniversalAI.Core;
using UniversalAI.Core.Capabilities;

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
        if (number > 1 && !automatic && !BenchmarkOutput.IsInputRedirected)
        {
            BenchmarkOutput.Write("\nНажмите Enter, чтобы перейти к следующему этапу... ");
            BenchmarkOutput.ReadLine();
        }

        BenchmarkOutput.WriteLine($"\n{new string('=', 72)}\nШАГ {number}. {title}\n{new string('=', 72)}");
    }

    public async Task<ChatResult> SendMessageAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        int number = ++_callNumber;
        BenchmarkOutput.WriteLine($"\n[{DateTimeOffset.Now:HH:mm:ss}] HTTP/API #{number} — {CallLabel}");
        BenchmarkOutput.WriteLine($"Модель: {request.Model}; сообщений в запросе: {request.Messages.Count}; " +
                          $"оценка входа: ~{TokenUsageCalculator.EstimateRequest(request)} токенов.");
        BenchmarkOutput.WriteLine("Отправляем запрос провайдеру, ожидаем ответ...");
        Stopwatch watch = Stopwatch.StartNew();
        ChatResult response = await provider.SendMessageAsync(request, cancellationToken);
        watch.Stop();
        BenchmarkOutput.WriteLine($"Ответ API за {watch.Elapsed.TotalSeconds:F2} с; finish={response.FinishReason}.");
        BenchmarkOutput.WriteLine(response.Content);
        bool measured = response.PromptTokens > 0 || response.CompletionTokens > 0;
        BenchmarkOutput.WriteLine(measured
            ? $"Токены API: вход {response.PromptTokens}, выход {response.CompletionTokens}, " +
              $"всего {response.PromptTokens + response.CompletionTokens}."
            : "Токены API: провайдер не вернул usage; расход не измерен.");
        return response;
    }

    public IAsyncEnumerable<string> StreamMessageAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
        provider.StreamMessageAsync(request, cancellationToken);
}
