using System.Net.Http.Json;
using System.Text.Json;
using UniversalAI.Services.Agents;

namespace UniversalAI.Services;

public sealed class RemoteWorkspaceClient(HttpClient http) : IWorkspaceClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"Сервер ({(int)response.StatusCode}): {body}");
            }
            return await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new InvalidDataException("Пустой ответ сервера.");
        }
    }
    private Task<T> Get<T>(string path, CancellationToken ct) => ReadResponse<T>(http.GetAsync(path, ct), ct);
    private async Task<T> ReadResponse<T>(Task<HttpResponseMessage> pending, CancellationToken ct) => await ReadAsync<T>(await pending, ct);
    private Task<T> Post<T>(string path, object value, CancellationToken ct) => ReadResponse<T>(http.PostAsJsonAsync(path, value, Json, ct), ct);
    public async Task<IReadOnlyList<ProviderInfo>> ProvidersAsync(CancellationToken ct = default) => await Get<List<ProviderInfo>>("api/providers", ct);
    public async Task<IReadOnlyList<AgentInfo>> AgentsAsync(CancellationToken ct = default) => await Get<List<AgentInfo>>("api/agents", ct);
    public async Task<IReadOnlyList<string>> ModelsAsync(int id, CancellationToken ct = default) => await Get<List<string>>($"api/conversations/{id}/models", ct);
    public Task<TokenStatistics> TokensAsync(int id, CancellationToken ct = default) => Get<TokenStatistics>($"api/conversations/{id}/tokens", ct);
    public async Task<IReadOnlyList<ConversationInfo>> ListAsync(CancellationToken ct = default) => await Get<List<ConversationInfo>>("api/conversations", ct);
    public Task<ConversationInfo> CreateAsync(CreateConversationRequest request, CancellationToken ct = default) => Post<ConversationInfo>("api/conversations", request, ct);
    public Task<ConversationInfo> GetAsync(int id, CancellationToken ct = default) => Get<ConversationInfo>($"api/conversations/{id}", ct);
    public Task<MessagePage> MessagesAsync(int id, int offset = 0, int limit = 100, CancellationToken ct = default) => Get<MessagePage>($"api/conversations/{id}/messages?offset={offset}&limit={limit}", ct);
    public Task<ConversationInfo> UpdateAsync(int id, ConversationSettings settings, CancellationToken ct = default) =>
        ReadResponse<ConversationInfo>(http.PutAsJsonAsync($"api/conversations/{id}/settings", settings, Json, ct), ct);
    public async Task ResetAsync(int id, CancellationToken ct = default) => _ = await Post<JsonElement>($"api/conversations/{id}/reset", new { }, ct);
    public Task<AgentTurnResult> SendAsync(int id, string content, CancellationToken ct = default) => Post<AgentTurnResult>($"api/conversations/{id}/messages", new SendMessageRequest(content), ct);
    public Task<ConversationInfo> CompressAsync(int id, CancellationToken ct = default) => Post<ConversationInfo>($"api/conversations/{id}/compress", new { }, ct);
    public Task<ConversationInfo> BranchAsync(int id, string title, CancellationToken ct = default) => Post<ConversationInfo>($"api/conversations/{id}/branch", new { title }, ct);
}
