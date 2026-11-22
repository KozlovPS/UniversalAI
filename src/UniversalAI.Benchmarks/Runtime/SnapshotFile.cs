using System.Text;
using System.Collections.Concurrent;

namespace UniversalAI.Benchmarks.Runtime;

/// <summary>Coordinates complete reads, downloads and atomic replacement of run snapshots.</summary>
public static class SnapshotFile
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static SemaphoreSlim Gate(string path) => Gates.GetOrAdd(Path.GetFullPath(path), _ => new(1, 1));

    public static async Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
    {
        var gate = Gate(path);
        await gate.WaitAsync(ct);
        try
        {
            return new ReadLease(new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan), gate);
        }
        catch { gate.Release(); throw; }
    }

    public static async Task<string> ReadAllTextAsync(string path, CancellationToken ct = default)
    {
        await using var file = await OpenReadAsync(path, ct);
        using var reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct);
    }

    public static async Task WriteAllTextAsync(string path, string content, CancellationToken ct = default)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var gate = Gate(path);
        await gate.WaitAsync(ct);
        try
        {
            await File.WriteAllTextAsync(temporary, content, ct);
            // A download, external reader or Windows indexer may still hold a handle without
            // FILE_SHARE_DELETE. Retry only the rename; never repeat the model request.
            for (int attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try { File.Move(temporary, path, overwrite: true); return; }
                catch (Exception e) when (attempt < 10 && IsTransientWindowsLock(e))
                { await Task.Delay(25 * (attempt + 1), ct); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { throw new IOException($"Не удалось обновить снимок '{path}': {e.Message}", e); }
            }
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Preserve the original failure. */ }
            gate.Release();
        }
    }

    private static bool IsTransientWindowsLock(Exception e) => OperatingSystem.IsWindows() &&
        (e is IOException or UnauthorizedAccessException) && (e.HResult & 0xffff) is 5 or 32 or 33;

    // Keep the snapshot lease until the HTTP download / deserializer closes its stream.
    private sealed class ReadLease(Stream inner, SemaphoreSlim gate) : Stream
    {
        private int _disposed;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            { try { inner.Dispose(); } finally { gate.Release(); } }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            { try { await inner.DisposeAsync(); } finally { gate.Release(); } }
            GC.SuppressFinalize(this);
        }
    }
}
