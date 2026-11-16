using System.Text.Json;

namespace UniversalAI.Configuration;

public sealed record ModelPrice(string Tier, string Model, decimal Input, decimal Output);

public sealed class PricingCatalog
{
    public string Snapshot { get; init; } = "";
    public string Source { get; init; } = "";
    public List<ModelPrice> Models { get; init; } = [];
    public static PricingCatalog Default { get; } = Load();

    private static PricingCatalog Load()
    {
        string path = Environment.GetEnvironmentVariable("UNIVERSALAI_PRICING_FILE")
            ?? Path.Combine(AppContext.BaseDirectory, "pricing.json");
        return JsonSerializer.Deserialize<PricingCatalog>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("Пустой каталог тарифов.");
    }

    public decimal? Estimate(string? model, int input, int output)
    {
        static string Normalize(string value) => value.StartsWith("openai/", StringComparison.OrdinalIgnoreCase)
            ? value[7..] : value;
        ModelPrice? price = Models.FirstOrDefault(p => string.Equals(Normalize(p.Model),
            Normalize(model?.Trim() ?? ""), StringComparison.OrdinalIgnoreCase));
        return price == null ? null : (input * price.Input + output * price.Output) / 1_000_000m;
    }
}
