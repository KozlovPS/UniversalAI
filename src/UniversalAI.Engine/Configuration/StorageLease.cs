namespace UniversalAI.Configuration;

/// <summary>One process owns a data directory. Other clients connect through its API.</summary>
public sealed class StorageLease : IDisposable
{
    private readonly FileStream _stream;
    public StorageLease(AppPaths paths)
    {
        paths.EnsureCreated();
        try { _stream = new FileStream(Path.Combine(paths.Root, ".owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) { throw new InvalidOperationException("Каталог данных уже открыт другим процессом. Подключитесь к серверу через --server URL или выберите другой UNIVERSALAI_DATA_DIR.", e); }
    }
    public void Dispose() => _stream.Dispose();
}
