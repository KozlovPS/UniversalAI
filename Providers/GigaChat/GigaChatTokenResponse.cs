using System.Text.Json.Serialization;

namespace UniversalAI.Providers.GigaChat
{
    internal class GigaChatTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = "";

        [JsonPropertyName("expires_at")]
        public long ExpiresAt { get; set; }
    }
}
