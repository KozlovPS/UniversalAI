
namespace UniversalAI.Providers.OpenAICompatible;

public class OpenAICompatibleOptions
{
    public string Name { get; set; } = "ProxyAPI";

    public string BaseUrl { get; set; } =
        "https://api.proxyapi.ru/v1";

    public string ApiKeyEnvironmentVariable { get; set; } =
        "PROXYAPI_API_KEY";

    public string DefaultModel { get; set; } =
        "deepseek/deepseek-v4.1-flash";

    public static OpenAICompatibleOptions FromEnvironment()
    {
        return new OpenAICompatibleOptions();
    }
}
