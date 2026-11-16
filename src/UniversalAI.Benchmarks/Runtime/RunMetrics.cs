using System.Text.Json;
using UniversalAI.Configuration;

namespace UniversalAI.Benchmarks.Runtime;

public sealed record RunMetrics(int Calls, int Errors, int AnswerCalls, int MemoryCalls,
    long? PromptTokens, long? CompletionTokens, long? MemoryTokens, decimal? EstimatedRubles,
    double TotalApiSeconds);

/// <summary>Reads completed records only, preserving unknown usage instead of turning it into zero.</summary>
public sealed class RunMetricsReader(FileRunStore store)
{
    public async Task<RunMetrics> ReadAsync(string id, CancellationToken ct = default)
    {
        var run = await store.GetAsync(id, ct);
        string path = Path.Combine(store.GetArtifactsDirectory(id), "api-calls.jsonl");
        if (!File.Exists(path)) return new(0, 0, 0, 0, null, null, null, null, 0);
        int calls = 0, errors = 0, answers = 0, memories = 0;
        long prompt = 0, completion = 0, memory = 0;
        decimal cost = 0; double milliseconds = 0; bool usageComplete = true, costComplete = true;
        var pricingPath = Path.Combine(store.GetDirectory(id), "pricing.json");
        var pricing = File.Exists(pricingPath)
            ? JsonSerializer.Deserialize<PricingCatalog>(await File.ReadAllTextAsync(pricingPath, ct), FileRunStore.Json)!
            : PricingCatalog.Default;
        // A writer may be appending a record while the UI refreshes.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(line); } catch (JsonException) { continue; }
            using (document)
            {
                var entry = document.RootElement; calls++;
                bool isMemory = entry.GetProperty("kind").GetString() == "memory";
                if (isMemory) memories++; else answers++;
                milliseconds += entry.GetProperty("milliseconds").GetDouble();
                var response = entry.GetProperty("response");
                if (response.ValueKind == JsonValueKind.Null) { errors++; usageComplete = costComplete = false; continue; }
                int input = response.GetProperty("PromptTokens").GetInt32(), output = response.GetProperty("CompletionTokens").GetInt32();
                if (input == 0 && output == 0) usageComplete = costComplete = false;
                prompt += input; completion += output;
                if (isMemory) memory += input + output;
                string model = entry.TryGetProperty("model", out var modelId) ? modelId.GetString() ?? run.Model : run.Model;
                decimal? estimated = pricing.Estimate(model, input, output);
                if (estimated.HasValue) cost += estimated.Value; else costComplete = false;
            }
        }
        return new(calls, errors, answers, memories, usageComplete ? prompt : null, usageComplete ? completion : null,
            usageComplete ? memory : null, costComplete ? cost : null, milliseconds / 1000);
    }
}
