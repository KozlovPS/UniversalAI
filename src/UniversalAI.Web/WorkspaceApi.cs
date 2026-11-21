using UniversalAI.Benchmarks.Runtime;
using UniversalAI.Services;

namespace UniversalAI.Web;

public sealed record SubmitRunRequest(string BenchmarkId, BenchmarkSettings Settings);
public sealed record BranchRequest(string Title);

public static class WorkspaceApi
{
    public static void MapWorkspaceApi(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/health", () => new { status = "ready", version = "1" });
        api.MapGet("/providers", (IWorkspaceClient workspace, CancellationToken ct) => workspace.ProvidersAsync(ct));
        api.MapGet("/agents", (IWorkspaceClient workspace, CancellationToken ct) => workspace.AgentsAsync(ct));
        api.MapGet("/conversations", (IWorkspaceClient workspace, CancellationToken ct) => workspace.ListAsync(ct));
        api.MapPost("/conversations", (CreateConversationRequest request, IWorkspaceClient workspace, CancellationToken ct) => workspace.CreateAsync(request, ct));
        api.MapGet("/conversations/{id:int}", (int id, IWorkspaceClient workspace, CancellationToken ct) => workspace.GetAsync(id, ct));
        api.MapDelete("/conversations/{id:int}", async (int id, IWorkspaceClient workspace, CancellationToken ct) =>
        { await workspace.DeleteAsync(id, ct); return Results.NoContent(); });
        api.MapPut("/conversations/{id:int}/settings", (int id, ConversationSettings request, IWorkspaceClient workspace, CancellationToken ct) => workspace.UpdateAsync(id, request, ct));
        api.MapGet("/conversations/{id:int}/messages", (int id, int? offset, int? limit, IWorkspaceClient workspace, CancellationToken ct) => workspace.MessagesAsync(id, offset ?? 0, limit ?? 100, ct));
        api.MapPost("/conversations/{id:int}/messages", (int id, SendMessageRequest request, IWorkspaceClient workspace, CancellationToken ct) => workspace.SendAsync(id, request.Content, ct));
        api.MapPost("/conversations/{id:int}/reset", async (int id, IWorkspaceClient workspace, CancellationToken ct) => { await workspace.ResetAsync(id, ct); return Results.Ok(new { cleared = true }); });
        api.MapPost("/conversations/{id:int}/compress", (int id, IWorkspaceClient workspace, CancellationToken ct) => workspace.CompressAsync(id, ct));
        api.MapPost("/conversations/{id:int}/branch", (int id, BranchRequest request, IWorkspaceClient workspace, CancellationToken ct) => workspace.BranchAsync(id, request.Title, ct));
        api.MapGet("/conversations/{id:int}/models", (int id, IWorkspaceClient workspace, CancellationToken ct) => workspace.ModelsAsync(id, ct));
        api.MapGet("/conversations/{id:int}/tokens", (int id, IWorkspaceClient workspace, CancellationToken ct) => workspace.TokensAsync(id, ct));
        api.MapGet("/benchmarks", (BenchmarkRegistry registry) => registry.List());
        api.MapGet("/runs", async (int? offset, int? limit, FileRunStore store, CancellationToken ct) =>
            (await store.ListAsync(ct)).Skip(Math.Max(0, offset ?? 0)).Take(Math.Clamp(limit ?? 100, 1, 500)));
        api.MapPost("/runs", async (SubmitRunRequest request, BenchmarkQueue queue, CancellationToken ct) =>
        {
            if (request.Settings.TranscriptPath != null) throw new ArgumentException("Сервер использует встроенный сценарий. Произвольные пути доступны только автономному CLI.");
            var run = await queue.SubmitAsync(request.BenchmarkId, request.Settings, ct);
            return Results.Accepted($"/api/runs/{run.Id}", run);
        });
        api.MapGet("/runs/{id}", (string id, FileRunStore store, CancellationToken ct) => store.GetAsync(id, ct));
        api.MapDelete("/runs/{id}", async (string id, BenchmarkQueue queue, CancellationToken ct) =>
        { await queue.DeleteAsync(id, ct); return Results.NoContent(); });
        api.MapPost("/runs/{id}/cancel", async (string id, BenchmarkQueue queue, CancellationToken ct) => { await queue.CancelAsync(id, ct); return Results.Ok(new { requested = true }); });
        api.MapGet("/runs/{id}/artifacts", (string id, FileRunStore store, CancellationToken ct) => store.ArtifactsAsync(id, ct));
        api.MapGet("/runs/{id}/metrics", (string id, RunMetricsReader metrics, CancellationToken ct) => metrics.ReadAsync(id, ct));
        api.MapGet("/runs/{id}/artifacts/{name}", async (string id, string name, FileRunStore store, CancellationToken ct) =>
            Results.File(await store.ArtifactPathAsync(id, name, ct), "application/octet-stream", name));
        api.MapGet("/runs/{id}/log", async (string id, bool? full, FileRunStore store, CancellationToken ct) =>
        {
            _ = await store.GetAsync(id, ct);
            return Results.Text(await ReadLogTailAsync(Path.Combine(store.GetDirectory(id), "output.log"), ct, full ?? false), "text/plain");
        });
        app.MapGet("/logs/{id}", async (string id, FileRunStore store, CancellationToken ct) =>
        {
            _ = await store.GetAsync(id, ct);
            string path = Path.Combine(store.GetDirectory(id), "output.log");
            return File.Exists(path) ? Results.File(path, "text/plain; charset=utf-8", $"{id}-output.log") : Results.NotFound();
        }).RequireAuthorization();
        app.MapGet("/downloads/{id}/{name}", async (string id, string name, FileRunStore store, CancellationToken ct) =>
            Results.File(await store.ArtifactPathAsync(id, name, ct), "application/octet-stream", name)).RequireAuthorization();
    }

    public static async Task<string> ReadLogTailAsync(string path, CancellationToken ct = default, bool full = false)
    {
        if (!File.Exists(path)) return "";
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (!full && stream.Length > 65536) stream.Seek(-65536, SeekOrigin.End);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }
}
