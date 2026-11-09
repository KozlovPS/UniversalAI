using UniversalAI.Core;
using UniversalAI.Data.Entities;
using UniversalAI.Providers;
using UniversalAI.Services.Providers;

namespace UniversalAI.Services.Agents;

/// <summary>Runs the model/tool/model loop for one user message.</summary>
public sealed class ToolCallingAgent
{
    private const int MaxToolRounds = 8;

    private readonly ConversationService _conversations;
    private readonly IProviderResolver _providers;
    private readonly AgentToolRegistry _tools;

    public ToolCallingAgent(
        ConversationService conversations,
        IProviderResolver providers,
        AgentToolRegistry tools)
    {
        _conversations = conversations;
        _providers = providers;
        _tools = tools;
    }

    public async Task<AgentTurnResult> RunAsync(
        int conversationId,
        string content,
        CancellationToken cancellationToken = default)
    {
        ConversationEntity conversation =
            await _conversations.GetConversationAsync(conversationId)
            ?? throw new InvalidOperationException("Текущий диалог не найден.");

        List<ChatMessage> history = await _conversations.GetMessagesAsync(
            conversationId,
            cancellationToken);

        ChatMessage userMessage = new()
        {
            Role = "user",
            Content = content
        };

        List<ChatMessage> messages =
        [
            new ChatMessage
            {
                Role = "system",
                Content = conversation.SystemPrompt
            },
            .. history,
            userMessage
        ];

        IAIProvider provider = await _providers.GetProviderForConversationAsync(
            conversationId,
            cancellationToken);

        if (!provider.Capabilities.SupportsFunctionCalling)
        {
            throw new NotSupportedException(
                $"Провайдер {provider.Name} не поддерживает вызов функций.");
        }

        int toolRounds = 0;
        int promptTokens = 0;
        int completionTokens = 0;
        int totalTokens = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ChatRequest request = new()
            {
                Model = conversation.Model,
                Messages = messages,
                Tools = _tools.Definitions.ToList(),
                Temperature = conversation.Temperature,
                MaxTokens = conversation.MaxTokens,
                TopP = conversation.TopP,
                FrequencyPenalty = conversation.FrequencyPenalty,
                PresencePenalty = conversation.PresencePenalty,
                Seed = conversation.Seed,
                ReasoningEffort = conversation.ReasoningEffort,
                ReasoningBudgetTokens = conversation.ReasoningBudgetTokens
            };

            ChatResult response = await provider.SendMessageAsync(
                request,
                cancellationToken);

            promptTokens += response.PromptTokens;
            completionTokens += response.CompletionTokens;
            totalTokens += response.TotalTokens;

            if (response.ToolCalls.Count == 0)
            {
                if (response.FinishReason is "tool_calls" or "function_call")
                {
                    throw new InvalidOperationException(
                        "Модель сообщила о вызове функции, но не вернула его данные.");
                }

                ChatResult finalResponse = new()
                {
                    Content = response.Content,
                    FinishReason = response.FinishReason,
                    PromptTokens = promptTokens,
                    CompletionTokens = completionTokens,
                    TotalTokens = totalTokens
                };

                await _conversations.SaveMessageAsync(
                    conversationId,
                    userMessage,
                    cancellationToken);
                await _conversations.SaveMessageAsync(
                    conversationId,
                    new ChatMessage
                    {
                        Role = "assistant",
                        Content = finalResponse.Content
                    },
                    cancellationToken);

                return new AgentTurnResult(
                    provider.Name,
                    conversation.Model,
                    finalResponse,
                    toolRounds);
            }

            if (toolRounds >= MaxToolRounds)
            {
                throw new InvalidOperationException(
                    $"Агент превысил лимит в {MaxToolRounds} циклов вызова инструментов.");
            }

            toolRounds++;
            messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = response.Content,
                ToolCalls = response.ToolCalls,
                FunctionsStateId = response.FunctionsStateId
            });

            foreach (ChatToolCall toolCall in response.ToolCalls)
            {
                string toolOutput = _tools.Execute(
                    toolCall.Function.Name,
                    toolCall.Function.Arguments);

                messages.Add(new ChatMessage
                {
                    Role = "tool",
                    ToolCallId = toolCall.Id,
                    FunctionName = toolCall.Function.Name,
                    Content = toolOutput
                });
            }
        }
    }
}
