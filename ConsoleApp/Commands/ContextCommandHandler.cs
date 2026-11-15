using UniversalAI.Services;
using UniversalAI.Services.Providers;

namespace UniversalAI.ConsoleApp.Commands;

public sealed class ContextCommandHandler(
    ConversationService conversations,
    IProviderResolver providers,
    ContextCompressionService context) : IConsoleCommandHandler
{
    public async Task<bool> TryHandleAsync(
        string input, ConsoleChatSession session, CancellationToken cancellationToken = default)
    {
        string[] parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        if (parts[0].Equals("/summary", StringComparison.OrdinalIgnoreCase))
        {
            var summary = await conversations.GetSummaryAsync(session.ConversationId, cancellationToken);
            Console.WriteLine(summary == null || string.IsNullOrEmpty(summary.Content)
                ? "Summary ещё не создано."
                : $"Summary #{summary.Revision}, покрыто {summary.CoveredMessageCount} сообщений:\n{summary.Content}");
            return true;
        }

        if (!parts[0].Equals("/compression", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            if (parts.Length == 2 && parts[1].ToLowerInvariant() is "on" or "off")
            {
                await conversations.SetCompressionAsync(session.ConversationId,
                    enabled: parts[1].Equals("on", StringComparison.OrdinalIgnoreCase), cancellationToken: cancellationToken);
            }
            else if (parts.Length == 3 && int.TryParse(parts[2], out int number) &&
                     parts[1].ToLowerInvariant() is "keep" or "batch")
            {
                await conversations.SetCompressionAsync(session.ConversationId,
                    keep: parts[1].Equals("keep", StringComparison.OrdinalIgnoreCase) ? number : null,
                    batch: parts[1].Equals("batch", StringComparison.OrdinalIgnoreCase) ? number : null,
                    cancellationToken: cancellationToken);
            }
            else if (parts.Length == 2 && parts[1].Equals("now", StringComparison.OrdinalIgnoreCase))
            {
                var conversation = await conversations.GetConversationAsync(session.ConversationId)
                    ?? throw new InvalidOperationException("Диалог не найден.");
                if (!conversation.CompressionEnabled)
                    throw new InvalidOperationException("Сначала включите /compression on.");
                var history = await conversations.GetMessagesAsync(session.ConversationId, cancellationToken);
                var provider = await providers.GetProviderForConversationAsync(session.ConversationId, cancellationToken);
                PreparedContext prepared = await context.PrepareAsync(conversation, history, provider, cancellationToken, force: true);
                Console.WriteLine($"История: ~{prepared.RawHistoryTokens} → ~{prepared.EffectiveHistoryTokens} токенов; " +
                                  $"summary {(prepared.SummaryCall == null ? "не обновлялось" : "обновлено")}.");
            }
            else if (parts.Length != 1)
            {
                throw new ArgumentException("Использование: /compression [on|off|keep N|batch N|now]");
            }

            var settings = await conversations.GetConversationAsync(session.ConversationId);
            Console.WriteLine($"Сжатие: {(settings!.CompressionEnabled ? "вкл" : "выкл")}; " +
                              $"keep={settings.ContextKeepLastMessages}; batch={settings.ContextCompressionBatchSize}.");
            Console.WriteLine("Сохраняются целые ходы: минимум keep сообщений плюс ещё не накопившийся batch.");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or HttpRequestException or NotSupportedException)
        {
            Console.WriteLine($"Сжатие: {exception.Message}");
        }

        return true;
    }
}
