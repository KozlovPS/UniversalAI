using System.Text.Json;
using System.Text.Json.Serialization;
using UniversalAI.Configuration;

namespace UniversalAI.Benchmarks.Runtime;

/// <summary>Metadata is independent of SQLite and survives the учебная database reset.</summary>
public sealed class FileRunStore(AppPaths paths)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() }
    };
    private readonly SemaphoreSlim _writes = new(1, 1);

    public string GetDirectory(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Неверный RunId.");
        return Path.Combine(paths.Runs, id);
    }
    public string GetArtifactsDirectory(string id) => Path.Combine(GetDirectory(id), "artifacts");
    public async Task SaveAsync(BenchmarkRun run, CancellationToken ct = default)
    {
        await _writes.WaitAsync(ct);
        try
        {
            var directory = GetDirectory(run.Id); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "run.json");
            string temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(run, Json), ct);
            File.Move(temporary, path, overwrite: true);
        }
        finally { _writes.Release(); }
    }
    public async Task<BenchmarkRun> GetAsync(string id, CancellationToken ct = default)
    {
        string path = Path.Combine(GetDirectory(id), "run.json");
        if (!File.Exists(path)) throw new KeyNotFoundException("Запуск не найден.");
        // Readers must allow atomic replacement of run.json while the runner records progress.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<BenchmarkRun>(stream, Json, ct)
            ?? throw new InvalidDataException("Пустые метаданные запуска.");
    }
    public async Task<IReadOnlyList<BenchmarkRun>> ListAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(paths.Runs)) return [];
        List<BenchmarkRun> runs = [];
        foreach (var directory in Directory.EnumerateDirectories(paths.Runs))
        {
            ct.ThrowIfCancellationRequested();
            string id = Path.GetFileName(directory);
            if (!Guid.TryParseExact(id, "N", out _)) continue;
            try { runs.Add(await GetAsync(id, ct)); }
            catch (Exception e) when (e is JsonException or IOException or KeyNotFoundException) { /* Ignore incomplete external imports. */ }
        }
        return runs.OrderByDescending(r => r.CreatedAt).ToList();
    }
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await _writes.WaitAsync(ct);
        try
        {
            var run = await GetAsync(id, ct);
            if (run.Status is RunStatus.Running or RunStatus.Queued)
                throw new InvalidOperationException("Сначала остановите прогон и дождитесь завершения.");
            string directory = Path.GetFullPath(GetDirectory(id));
            string root = Path.GetFullPath(paths.Runs).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Небезопасный каталог прогона.");
            ct.ThrowIfCancellationRequested();
            Directory.Delete(directory, recursive: true);
        }
        finally { _writes.Release(); }
    }
    public async Task<IReadOnlyList<string>> ArtifactsAsync(string id, CancellationToken ct = default)
    {
        _ = await GetAsync(id, ct);
        var directory = GetArtifactsDirectory(id);
        return Directory.Exists(directory) ? Directory.GetFiles(directory).Select(Path.GetFileName).OfType<string>().Order().ToList() : [];
    }
    public async Task RecoverInterruptedAsync(CancellationToken ct = default)
    {
        foreach (var run in await ListAsync(ct))
            if (run.Status is RunStatus.Running or RunStatus.Queued)
                await SaveAsync(run with { Status = RunStatus.Interrupted, FinishedAt = DateTimeOffset.UtcNow,
                    Error = "Предыдущий процесс остановлен. Частичные результаты сохранены; повторите запуск явно." }, ct);
    }
    public async Task<string> ArtifactPathAsync(string id, string name, CancellationToken ct = default)
    {
        if (name != Path.GetFileName(name) || name.IndexOfAny(['/', '\\', ':']) >= 0 || name is "." or "..")
            throw new ArgumentException("Неверное имя артефакта.");
        if (!(await ArtifactsAsync(id, ct)).Contains(name)) throw new KeyNotFoundException("Артефакт не найден.");
        return Path.Combine(GetArtifactsDirectory(id), name);
    }
}
