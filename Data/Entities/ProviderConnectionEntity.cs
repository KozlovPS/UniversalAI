
namespace UniversalAI.Data.Entities;

public class ProviderConnectionEntity
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public string ProviderType { get; set; } = "";

    public string BaseUrl { get; set; } = "";

    public string ApiKeyEnvironmentVariable { get; set; } = "";

    public string DefaultModel { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
