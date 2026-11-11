using UniversalAI.Core;
using UniversalAI.Data.Entities;
using UniversalAI.Providers;
using UniversalAI.Services.Providers;
using System.Net;

namespace UniversalAI.Services.Agents;

/// <summary>Runs the model/tool/model loop for one user message.</summary>
public sealed class ToolCallingAgent : IAgent
{
    private const int MaxToolRounds = 8;

    public const string AgentId = "tools";

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

    public string Id => AgentId;

    public string DisplayName => "Агент с инструментами";

    public string Description => "Сам выбирает время, калькулятор или бросок кубика.";

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
        List<ChatMessage> turnMessages = [userMessage];

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
        int estimatedPromptTokens = 0;
        List<AgentModelCallTrace> modelCalls = [];

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

            int currentEstimatedPrompt = TokenUsageCalculator.EstimateRequest(request);
            if (modelCalls.Count == 0)
            {
                estimatedPromptTokens = currentEstimatedPrompt;
            }

            ChatResult response;
            try
            {
                response = await provider.SendMessageAsync(
                    request,
                    cancellationToken);
            }
            catch (HttpRequestException exception) when (
                TokenUsageCalculator.IsContextLimitError(exception.Message))
            {
                throw new ModelContextLimitException(
                    request.Model,
                    currentEstimatedPrompt,
                    exception);
            }

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

                modelCalls.Add(new AgentModelCallTrace(
                    modelCalls.Count + 1,
                    response.FinishReason,
                    response.PromptTokens,
                    response.CompletionTokens,
                    response.TotalTokens,
                    [])
                {
                    EstimatedPromptTokens = currentEstimatedPrompt
                });

                turnMessages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = finalResponse.Content,
                    UsageModel = promptTokens > 0 || completionTokens > 0
                        ? conversation.Model
                        : null,
                    UsagePromptTokens = promptTokens > 0 || completionTokens > 0
                        ? promptTokens
                        : null,
                    UsageCompletionTokens = promptTokens > 0 || completionTokens > 0
                        ? completionTokens
                        : null
                });
                await _conversations.SaveMessagesAsync(
                    conversationId,
                    turnMessages,
                    cancellationToken);

                AgentTokenUsage tokenUsage = TokenUsageCalculator.CreateTurnUsage(
                    conversation.Model,
                    content,
                    history,
                    [.. history, .. turnMessages],
                    estimatedPromptTokens,
                    promptTokens,
                    completionTokens,
                    totalTokens);

                return new AgentTurnResult(
                    provider.Name,
                    conversation.Model,
                    finalResponse,
                    toolRounds,
                    modelCalls)
                {
                    TokenUsage = tokenUsage
                };
            }

            if (toolRounds >= MaxToolRounds)
            {
                throw new InvalidOperationException(
                    $"Агент превысил лимит в {MaxToolRounds} циклов вызова инструментов.");
            }

            toolRounds++;
            ChatMessage assistantToolCallMessage = new()
            {
                Role = "assistant",
                Content = response.Content,
                ToolCalls = response.ToolCalls,
                FunctionsStateId = response.FunctionsStateId
            };
            messages.Add(assistantToolCallMessage);
            turnMessages.Add(assistantToolCallMessage);

            List<AgentToolCallTrace> toolCallTraces = [];
            foreach (ChatToolCall toolCall in response.ToolCalls)
            {
                string toolOutput = _tools.Execute(
                    toolCall.Function.Name,
                    toolCall.Function.Arguments);

                toolCallTraces.Add(new AgentToolCallTrace(
                    toolCall.Function.Name,
                    toolCall.Function.Arguments,
                    toolOutput));

                ChatMessage toolResultMessage = new()
                {
                    Role = "tool",
                    ToolCallId = toolCall.Id,
                    FunctionName = toolCall.Function.Name,
                    Content = toolOutput
                };
                messages.Add(toolResultMessage);
                turnMessages.Add(toolResultMessage);
            }

            modelCalls.Add(new AgentModelCallTrace(
                modelCalls.Count + 1,
                response.FinishReason,
                response.PromptTokens,
                response.CompletionTokens,
                response.TotalTokens,
                toolCallTraces)
            {
                EstimatedPromptTokens = currentEstimatedPrompt
            });
        }
    }
}
