using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace UniversalAI.Data;

/// <summary>Learning-project policy: recreate incompatible databases; benchmark files remain intact.</summary>
public static class DatabaseInitializer
{
    public static async Task InitializeAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        bool created = await db.Database.EnsureCreatedAsync(cancellationToken);
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(db.Database.GenerateCreateScript())));
        string? saved = null;
        var connection = db.Database.GetDbConnection();
        bool close = connection.State != ConnectionState.Open;
        if (close) await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='__UniversalAISchema'";
            bool hasMetadata = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
            if (hasMetadata)
            {
                command.CommandText = "SELECT Fingerprint FROM __UniversalAISchema LIMIT 1";
                saved = await command.ExecuteScalarAsync(cancellationToken) as string;
            }
        }
        finally { if (close) await db.Database.CloseConnectionAsync(); }
        if (!created && saved != fingerprint)
        {
            Console.WriteLine("Схема учебной SQLite-базы изменилась. Пересоздаём БД; файлы бенчмарков сохраняются.");
            await db.Database.EnsureDeletedAsync(cancellationToken);
            await db.Database.EnsureCreatedAsync(cancellationToken);
        }
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS __UniversalAISchema (Fingerprint TEXT NOT NULL)", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM __UniversalAISchema", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO __UniversalAISchema (Fingerprint) VALUES ({fingerprint})", cancellationToken);
    }
}
