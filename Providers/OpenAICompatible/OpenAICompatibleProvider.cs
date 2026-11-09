
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UniversalAI.Core;
using UniversalAI.Core.Capabilities;

namespace UniversalAI.Providers.OpenAICompatible;

public class OpenAICompatibleProvider : IAIProvider
{
    private readonly HttpClient _http;
    private readonly OpenAICompatibleOptions _options;

    public string Name => _options.Name;

    public ProviderCapabilities Capabilities { get; } = new()
    {
        SupportsTemperature = true,
        SupportsMaxTokens = true,
        SupportsTopP = true,
        SupportsFrequencyPenalty = true,
        SupportsPresencePenalty = true,
        SupportsSeed = true,
        SupportsReasoningEffort = true,
        SupportsReasoningBudgetTokens = false,
        SupportsJsonSchema = false,
        SupportsStreaming = false,
        SupportsFunctionCalling = true
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    public OpenAICompatibleProvider(
        HttpClient http,
        OpenAICompatibleOptions options)
    {
        _http = http;
        _options = options;

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            throw new ArgumentException("Не указан Base URL.");
        }

        if (string.IsNullOrWhiteSpace(
            options.ApiKeyEnvironmentVariable))
        {
            throw new ArgumentException(
                "Не указано имя переменной окружения API-ключа.");
        }
    }

    private string GetApiKey()
    {
        string variableName = _options.ApiKeyEnvironmentVariable;

        string? key = Environment.GetEnvironmentVariable(
            variableName);

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                $"Не найдена переменная окружения {variableName}.");
        }

        if (key.Any(character =>
                !char.IsAscii(character) ||
                char.IsControl(character) ||
                char.IsWhiteSpace(character)))
        {
            throw new InvalidOperationException(
                $"Переменная окружения {variableName} содержит недопустимые " +
                "символы. API-ключ должен быть ASCII без пробелов; " +
                "проверьте значение, оно не выводится в лог.");
        }

        return key;
    }

    public void ValidateApiKey() => _ = GetApiKey();

    public async Task<ChatResult> SendMessageAsync(
        ChatRequest request,
        CancellationToken cancellationToken = default)
    {
        string model = string.IsNullOrWhiteSpace(request.Model)
            ? _options.DefaultModel
            : request.Model;

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException("Не указана модель.");
        }

        ModelCapabilities capabilities =
            ModelCapabilityResolver.Resolve(Name, model);

        ChatRequestValidator.Validate(
            new ChatRequest
            {
                Model = model,
                MaxTokens = request.MaxTokens,
                Temperature = request.Temperature,
                TopP = request.TopP,
                FrequencyPenalty = request.FrequencyPenalty,
                PresencePenalty = request.PresencePenalty,
                Seed = request.Seed,
                ReasoningEffort = request.ReasoningEffort,
                ReasoningBudgetTokens = request.ReasoningBudgetTokens
            },
            capabilities);

        if (request.ResponseFormat.HasValue)
        {
            throw new NotSupportedException(
                "JSON Schema пока не реализован для этого провайдера.");
        }

        OpenAICompatibleRequest body = new()
        {
            Model = model,
            Messages = request.Messages.Select(ToOpenAIMessage).ToList(),
            Tools = request.Tools.Count == 0
                ? null
                : request.Tools.Select(tool => new OpenAICompatibleTool
                {
                    Function = tool
                }).ToList(),
            ToolChoice = request.Tools.Count == 0 ? null : "auto",

            MaxTokens =
                capabilities.TokenLimitParameter ==
                TokenLimitParameter.MaxTokens
                    ? request.MaxTokens
                    : null,

            MaxCompletionTokens =
                capabilities.TokenLimitParameter ==
                TokenLimitParameter.MaxCompletionTokens
                    ? request.MaxTokens
                    : null,

            Temperature = capabilities.SupportsTemperature
                ? request.Temperature
                : null,

            TopP = capabilities.SupportsTopP
                ? request.TopP
                : null,

            FrequencyPenalty =
                capabilities.SupportsFrequencyPenalty
                    ? request.FrequencyPenalty
                    : null,

            PresencePenalty =
                capabilities.SupportsPresencePenalty
                    ? request.PresencePenalty
                    : null,

            Seed = capabilities.SupportsSeed
                ? request.Seed
                : null,

            ReasoningEffort =
                capabilities.SupportsReasoningEffort &&
                !string.IsNullOrWhiteSpace(request.ReasoningEffort)
                    ? request.ReasoningEffort.Trim().ToLowerInvariant()
                    : null,

            Thinking =
                model.Equals(
                    "deepseek/deepseek-v4.1-flash",
                    StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(request.ReasoningEffort)
                && !request.ReasoningEffort.Equals(
                    "none",
                    StringComparison.OrdinalIgnoreCase)
                    ? new { type = "enabled" }
                    : null,
        };

        string json = JsonSerializer.Serialize(body, JsonOptions);

        using HttpRequestMessage httpRequest = new(
            HttpMethod.Post,
            $"{_options.BaseUrl.TrimEnd('/')}/chat/completions");

        httpRequest.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", GetApiKey());

        httpRequest.Content = new StringContent(
            json,
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response =
            await _http.SendAsync(httpRequest, cancellationToken);

        string responseText =
            await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Ошибка API {Name}: HTTP {(int)response.StatusCode}\n" +
                responseText,
                null,
                response.StatusCode);
        }

        using JsonDocument document =
            JsonDocument.Parse(responseText);

        JsonElement root = document.RootElement;
        JsonElement choice = root.GetProperty("choices")[0];
        JsonElement message = choice.GetProperty("message");

        List<ChatToolCall> toolCalls = [];
        if (message.TryGetProperty("tool_calls", out JsonElement callsElement) &&
            callsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement callElement in callsElement.EnumerateArray())
            {
                JsonElement functionElement = callElement.GetProperty("function");
                string name = GetString(functionElement, "name", "");
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new InvalidOperationException(
                        "API вернул вызов инструмента без имени функции.");
                }

                toolCalls.Add(new ChatToolCall
                {
                    Id = GetString(callElement, "id", Guid.NewGuid().ToString("N")),
                    Function = new ChatFunctionCall
                    {
                        Name = name,
                        Arguments = GetJsonString(functionElement, "arguments", "{}")
                    }
                });
            }
        }

        JsonElement usage = root.TryGetProperty(
            "usage", out JsonElement usageElement)
                ? usageElement
                : default;

        return new ChatResult
        {
            Content = GetString(message, "content", ""),
            ToolCalls = toolCalls,
            FinishReason = GetString(
                choice, "finish_reason", "unknown"),
            PromptTokens = GetInt(usage, "prompt_tokens"),
            CompletionTokens = GetInt(usage, "completion_tokens"),
            TotalTokens = GetInt(usage, "total_tokens")
        };
    }

    public async Task<List<OpenAICompatibleModel>> GetModelsAsync(
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            $"{_options.BaseUrl.TrimEnd('/')}/models");

        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", GetApiKey());

        using HttpResponseMessage response =
            await _http.SendAsync(request, cancellationToken);

        string responseText =
            await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Ошибка получения моделей {Name}: " +
                $"HTTP {(int)response.StatusCode}\n{responseText}",
                null,
                response.StatusCode);
        }

        using JsonDocument document =
            JsonDocument.Parse(responseText);

        return !document.RootElement.TryGetProperty(
            "data", out JsonElement data)
            ? throw new InvalidOperationException(
                "API не вернул список моделей в поле data.")
            : JsonSerializer.Deserialize<List<OpenAICompatibleModel>>(
            data.GetRawText(),
            JsonOptions) ?? [];
    }

    private static string GetString(
        JsonElement element,
        string property,
        string fallback)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(property, out JsonElement value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
    }

    private static string GetJsonString(
        JsonElement element,
        string property,
        string fallback)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out JsonElement value))
        {
            return fallback;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : value.GetRawText();
    }

    private static OpenAICompatibleMessage ToOpenAIMessage(ChatMessage message)
    {
        return new OpenAICompatibleMessage
        {
            Role = message.Role,
            Content = message.Content,
            ToolCalls = message.ToolCalls,
            ToolCallId = message.ToolCallId
        };
    }

    private static int GetInt(
        JsonElement element,
        string property)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(property, out JsonElement value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt32(out int result)
            ? result
            : 0;
    }

    public IAsyncEnumerable<string> StreamMessageAsync(
        ChatRequest request,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            $"Streaming для {Name} пока не реализован.");
    }

    private class OpenAICompatibleRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "";

        [JsonPropertyName("messages")]
        public List<OpenAICompatibleMessage> Messages { get; set; } = [];

        [JsonPropertyName("tools")]
        public List<OpenAICompatibleTool>? Tools { get; set; }

        [JsonPropertyName("tool_choice")]
        public string? ToolChoice { get; set; }

        [JsonPropertyName("max_tokens")]
        public int? MaxTokens { get; set; }

        [JsonPropertyName("max_completion_tokens")]
        public int? MaxCompletionTokens { get; set; }

        [JsonPropertyName("temperature")]
        public double? Temperature { get; set; }

        [JsonPropertyName("top_p")]
        public double? TopP { get; set; }

        [JsonPropertyName("frequency_penalty")]
        public double? FrequencyPenalty { get; set; }

        [JsonPropertyName("presence_penalty")]
        public double? PresencePenalty { get; set; }

        [JsonPropertyName("seed")]
        public int? Seed { get; set; }

        [JsonPropertyName("reasoning_effort")]
        public string? ReasoningEffort { get; set; }

        [JsonPropertyName("thinking")]
        public object? Thinking { get; set; }
    }

    private sealed class OpenAICompatibleMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "";

        [JsonPropertyName("content")]
        public string Content { get; set; } = "";

        [JsonPropertyName("tool_calls")]
        public List<ChatToolCall>? ToolCalls { get; set; }

        [JsonPropertyName("tool_call_id")]
        public string? ToolCallId { get; set; }
    }

    private sealed class OpenAICompatibleTool
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "function";

        [JsonPropertyName("function")]
        public ChatFunctionDefinition Function { get; set; } = new();
    }
}
