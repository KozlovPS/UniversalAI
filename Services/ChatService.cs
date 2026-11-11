using UniversalAI.Core;
using UniversalAI.Data.Entities;
using UniversalAI.Providers;
using UniversalAI.Services.Providers;

namespace UniversalAI.Services;

/// <summary>Coordinates conversation history, provider calls, and persistence.</summary>
public sealed class ChatService
{
    private readonly ConversationService _conversations;
    private readonly ProviderManager _providers;

    public ChatService(
        ConversationService conversations,
        ProviderManager providers)
    {
        _conversations = conversations;
        _providers = providers;
    }

    public async Task<ChatTurnResult> SendMessageAsync(
        int conversationId,
        string content,
        CancellationToken cancellationToken = default)
    {
        ConversationEntity conversation =
            await _conversations.GetConversationAsync(conversationId)
            ?? throw new InvalidOperationException("Текущий диалог не найден.");

        List<ChatMessage> history =
            await _conversations.GetMessagesAsync(conversationId, cancellationToken);

        ChatMessage userMessage = new()
        {
            Role = "user",
            Content = content
        };

        ChatRequest request = new()
        {
            Model = conversation.Model,
            Messages =
            [
                new ChatMessage
                {
                    Role = "system",
                    Content = conversation.SystemPrompt
                },
                .. history,
                userMessage
            ],
            Temperature = conversation.Temperature,
            MaxTokens = conversation.MaxTokens,
            TopP = conversation.TopP,
            FrequencyPenalty = conversation.FrequencyPenalty,
            PresencePenalty = conversation.PresencePenalty,
            Seed = conversation.Seed,
            ReasoningEffort = conversation.ReasoningEffort,
            ReasoningBudgetTokens = conversation.ReasoningBudgetTokens
        };

        IAIProvider provider = await _providers.GetProviderForConversationAsync(
            conversationId,
            cancellationToken);

        ChatResult response;
        try
        {
            response = await provider.SendMessageAsync(request, cancellationToken);
        }
        catch (HttpRequestException exception) when (
            Agents.TokenUsageCalculator.IsContextLimitError(exception.Message))
        {
            throw new Agents.ModelContextLimitException(
                request.Model,
                Agents.TokenUsageCalculator.EstimateRequest(request),
                exception);
        }

        await _conversations.SaveMessagesAsync(
            conversationId,
            [
                userMessage,
                new ChatMessage
                {
                    Role = "assistant",
                    Content = response.Content,
                    UsageModel = response.PromptTokens > 0 || response.CompletionTokens > 0
                        ? request.Model
                        : null,
                    UsagePromptTokens = response.PromptTokens > 0 || response.CompletionTokens > 0
                        ? response.PromptTokens
                        : null,
                    UsageCompletionTokens = response.PromptTokens > 0 || response.CompletionTokens > 0
                        ? response.CompletionTokens
                        : null
                }
            ],
            cancellationToken);

        return new ChatTurnResult(provider.Name, request, response);
    }
}
