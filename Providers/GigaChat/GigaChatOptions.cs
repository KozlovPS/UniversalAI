
namespace UniversalAI.Providers.GigaChat;

public class GigaChatOptions
{
    public string AuthKey { get; set; } = "";

    public string Scope { get; set; } = "GIGACHAT_API_PERS";

    public string BaseUrl { get; set; } = "https://api.giga.chat/v1";

    public string DefaultModel { get; set; } = "GigaChat-2";

    public static GigaChatOptions FromEnvironment()
    {
        return new GigaChatOptions
        {
            AuthKey = Environment.GetEnvironmentVariable("GIGACHAT_AUTH_KEY") ?? ""
        };
    }
}
