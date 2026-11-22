using Microsoft.Extensions.Configuration;

namespace UniversalAI.Configuration;

/// <summary>Runtime data is independent of the checkout and current working directory.</summary>
public sealed class AppPaths
{
    public string Root { get; }
    public string Database => Path.Combine(Root, "universalai.db");
    public string Runs => Path.Combine(Root, "runs");

    public AppPaths(IConfiguration configuration)
    {
        string? configured = Environment.GetEnvironmentVariable("UNIVERSALAI_DATA_DIR")
            ?? configuration["Storage:RootPath"];
        Root = Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniversalAI")
            : Environment.ExpandEnvironmentVariables(configured));
    }

    public void EnsureCreated() => Directory.CreateDirectory(Root);
}
