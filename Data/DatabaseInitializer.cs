using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace UniversalAI.Data;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(
        AppDbContext db,
        CancellationToken cancellationToken = default)
    {
        await db.Database.EnsureCreatedAsync(cancellationToken);

        bool hasPayloadJson = await HasColumnAsync(
            db,
            "ChatMessages",
            "PayloadJson",
            cancellationToken);
        bool hasAgentId = await HasColumnAsync(
            db,
            "Conversations",
            "AgentId",
            cancellationToken);

        if (hasPayloadJson && hasAgentId)
        {
            return;
        }

        Console.WriteLine(
            "Обнаружена старая схема SQLite. База будет пересоздана без миграции.");

        await db.Database.EnsureDeletedAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);
    }

    private static async Task<bool> HasColumnAsync(
        AppDbContext db,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        DbConnection connection = db.Database.GetDbConnection();
        bool closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{tableName}\");";
            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(
                    reader.GetString(1),
                    columnName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            if (closeConnection)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }
}
