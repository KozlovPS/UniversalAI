
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UniversalAI.Core;
using UniversalAI.Core.Capabilities;

namespace UniversalAI.Providers.GigaChat;

public class GigaChatProvider : IAIProvider
{
    private readonly HttpClient _http;
    private readonly GigaChatOptions _options;

    private string? _accessToken;
    private DateTimeOffset _tokenExpiresAt;

    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    public string Name => "GigaChat";

    public ProviderCapabilities Capabilities { get; } = new()
    {
        SupportsTemperature = true,
        SupportsMaxTokens = true,

        SupportsTopP = false,
        SupportsFrequencyPenalty = false,
        SupportsPresencePenalty = false,
        SupportsSeed = false,

        SupportsReasoningEffort = false,
        SupportsReasoningBudgetTokens = false,

        SupportsJsonSchema = true,
        SupportsStreaming = false,
        SupportsFunctionCalling = true
    };

    private const string TokenUrl =
        "https://ngw.devices.sberbank.ru:9443/api/v2/oauth";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    public GigaChatProvider(
        HttpClient http,
        GigaChatOptions options)
    {
        _http = http;
        _options = options;
    }

    // =========================================
    // ОТПРАВКА СООБЩЕНИЯ
    // =========================================

    public async Task<ChatResult> SendMessageAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        string token = await GetAccessTokenAsync(
            cancellationToken);

        try
        {
            return await SendRequestAsync(request, token, cancellationToken);
        }
        catch (HttpRequestException ex)
            when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            // Токен мог истечь.
            // Получаем новый и повторяем запрос один раз.

            token = await GetAccessTokenAsync(cancellationToken, forceRefresh: true);

            return await SendRequestAsync(request, token, cancellationToken);
        }
    }

    // =========================================
    // ЗАПРОС К CHAT COMPLETIONS
    // =========================================

    private async Task<ChatResult> SendRequestAsync(
        ChatRequest request,
        string accessToken,
        CancellationToken cancellationToken)
    {
        GigaChatRequest body = new()
        {
            Model = string.IsNullOrWhiteSpace(request.Model)
                ? _options.DefaultModel
                : request.Model,

            Messages = request.Messages.Select(ToGigaChatMessage).ToList(),
            Functions = request.Tools.Count == 0 ? null : request.Tools,
            FunctionCall = request.Tools.Count == 0 ? null : "auto",

            MaxTokens = request.MaxTokens,

            Temperature = request.Temperature,

            ResponseFormat = request.ResponseFormat
        };

        string json = JsonSerializer.Serialize(
            body,
            JsonOptions);

        using HttpRequestMessage httpRequest = new(
            HttpMethod.Post,
            $"{_options.BaseUrl.TrimEnd('/')}/chat/completions");

        httpRequest.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);

        httpRequest.Content = new StringContent(
            json,
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response = await _http.SendAsync(
            httpRequest,
            cancellationToken);

        string responseText = await response.Content.ReadAsStringAsync(
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Ошибка GigaChat API: HTTP {(int)response.StatusCode}\n" +
                responseText,
                null,
                response.StatusCode);
        }

        using JsonDocument document = JsonDocument.Parse(responseText);

        JsonElement root = document.RootElement;

        JsonElement choice = root
            .GetProperty("choices")[0];

        JsonElement message = choice
            .GetProperty("message");

        List<ChatToolCall> toolCalls = [];
        if (message.TryGetProperty("function_call", out JsonElement functionCall) &&
            functionCall.ValueKind == JsonValueKind.Object)
        {
            string functionName = GetString(functionCall, "name");
            if (!string.IsNullOrWhiteSpace(functionName))
            {
                toolCalls.Add(new ChatToolCall
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Function = new ChatFunctionCall
                    {
                        Name = functionName,
                        Arguments = GetJsonString(functionCall, "arguments", "{}")
                    }
                });
            }
        }

        JsonElement usage = root
            .GetProperty("usage");

        return new ChatResult
        {
            Content = message
                .GetProperty("content")
                .ValueKind == JsonValueKind.String
                    ? message.GetProperty("content").GetString() ?? ""
                    : "",

            ToolCalls = toolCalls,

            FunctionsStateId = GetNullableString(message, "functions_state_id"),

            FinishReason = GetString(
                choice,
                "finish_reason"),

            PromptTokens = GetInt(
                usage,
                "prompt_tokens"),

            CompletionTokens = GetInt(
                usage,
                "completion_tokens"),

            TotalTokens = GetInt(
                usage,
                "total_tokens")
        };
    }

    // =========================================
    // ПОЛУЧЕНИЕ OAUTH TOKEN
    // =========================================

    private async Task<string> GetAccessTokenAsync(
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        await _tokenLock.WaitAsync(cancellationToken);

        try
        {
            if (!forceRefresh &&
                !string.IsNullOrWhiteSpace(_accessToken) &&
                DateTimeOffset.UtcNow < _tokenExpiresAt)
            {
                return _accessToken;
            }

            using HttpRequestMessage request = new(
                HttpMethod.Post,
                TokenUrl);

            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue(
                    "application/json"));

            request.Headers.Add(
                "RqUID",
                Guid.NewGuid().ToString());

            request.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Basic",
                    _options.AuthKey);

            request.Content = new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["scope"] = _options.Scope
                });

            using HttpResponseMessage response =
                await _http.SendAsync(
                    request,
                    cancellationToken);

            string responseText =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Ошибка получения GigaChat Token: " +
                    $"HTTP {(int)response.StatusCode}\n" +
                    responseText,
                    null,
                    response.StatusCode);
            }

            GigaChatTokenResponse? tokenResponse =
                JsonSerializer.Deserialize<GigaChatTokenResponse>(
                    responseText);

            if (tokenResponse == null ||
                string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
            {
                throw new Exception(
                    "GigaChat не вернул access_token.");
            }

            _accessToken = tokenResponse.AccessToken;

            // Оставляем небольшой запас до истечения токена.
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(25);

            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    // =========================================
    // ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ
    // =========================================

    private static string GetString(
        JsonElement element,
        string property)
    {
        return element.TryGetProperty(property, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "unknown"
            : "unknown";
    }

    private static string? GetNullableString(
        JsonElement element,
        string property)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(property, out JsonElement value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int GetInt(
        JsonElement element,
        string property)
    {
        return element.TryGetProperty(property, out JsonElement value) &&
            value.TryGetInt32(out int result)
            ? result
            : 0;
    }

    private static string GetJsonString(
        JsonElement element,
        string property,
        string fallback)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(property, out JsonElement value)
            ? value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? fallback
                : value.GetRawText()
            : fallback;
    }

    private static GigaChatMessage ToGigaChatMessage(ChatMessage message)
    {
        GigaChatMessage result = new()
        {
            Role = message.Role == "tool" ? "function" : message.Role,
            Content = message.Content,
            FunctionName = message.Role == "tool" ? message.FunctionName : null,
            FunctionsStateId = message.FunctionsStateId
        };

        if (message.ToolCalls is { Count: > 0 })
        {
            if (message.ToolCalls.Count > 1)
            {
                throw new NotSupportedException(
                    "GigaChat API в текущей версии провайдера поддерживает " +
                    "только один вызов функции за один ответ модели.");
            }

            ChatFunctionCall function = message.ToolCalls[0].Function;
            using JsonDocument arguments = JsonDocument.Parse(function.Arguments);
            result.FunctionCall = new GigaChatFunctionCall
            {
                Name = function.Name,
                Arguments = arguments.RootElement.Clone()
            };
        }

        return result;
    }

    private void ValidateRequest(ChatRequest request)
    {
        List<string> unsupported = [];

        if (request.TopP.HasValue && !Capabilities.SupportsTopP)
        {
            unsupported.Add("TopP");
        }

        if (request.FrequencyPenalty.HasValue &&
            !Capabilities.SupportsFrequencyPenalty)
        {
            unsupported.Add("FrequencyPenalty");
        }

        if (request.PresencePenalty.HasValue &&
            !Capabilities.SupportsPresencePenalty)
        {
            unsupported.Add("PresencePenalty");
        }

        if (request.Seed.HasValue && !Capabilities.SupportsSeed)
        {
            unsupported.Add("Seed");
        }

        if (request.ReasoningEffort != null &&
            !Capabilities.SupportsReasoningEffort)
        {
            unsupported.Add("ReasoningEffort");
        }

        if (request.ReasoningBudgetTokens.HasValue &&
            !Capabilities.SupportsReasoningBudgetTokens)
        {
            unsupported.Add("ReasoningBudgetTokens");
        }

        if (request.ResponseFormat.HasValue &&
            !Capabilities.SupportsJsonSchema)
        {
            unsupported.Add("JsonSchema");
        }

        if (unsupported.Count > 0)
        {
            throw new NotSupportedException(
                $"Провайдер {Name} не поддерживает настройки: " +
                string.Join(", ", unsupported));
        }
    }

    // =========================================
    // STREAMING
    // Пока не реализован
    // =========================================

    public IAsyncEnumerable<string> StreamMessageAsync(
        ChatRequest request,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            "Streaming для GigaChat будет реализован на этапе 8.");
    }

    // =========================================
    // DTO ДЛЯ GIGACHAT API
    // =========================================

    private class GigaChatRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "";

        [JsonPropertyName("messages")]
        public List<GigaChatMessage> Messages { get; set; } = [];

        [JsonPropertyName("functions")]
        public List<ChatFunctionDefinition>? Functions { get; set; }

        [JsonPropertyName("function_call")]
        public string? FunctionCall { get; set; }

        [JsonPropertyName("max_tokens")]
        public int? MaxTokens { get; set; }

        [JsonPropertyName("temperature")]
        public double? Temperature { get; set; }

        [JsonPropertyName("response_format")]
        public JsonElement? ResponseFormat { get; set; }
    }

    private sealed class GigaChatMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "";

        [JsonPropertyName("content")]
        public string Content { get; set; } = "";

        [JsonPropertyName("function_call")]
        public GigaChatFunctionCall? FunctionCall { get; set; }

        [JsonPropertyName("function_name")]
        public string? FunctionName { get; set; }

        [JsonPropertyName("functions_state_id")]
        public string? FunctionsStateId { get; set; }
    }

    private sealed class GigaChatFunctionCall
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("arguments")]
        public JsonElement Arguments { get; set; }
    }
}
