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

        DbConnection connection = db.Database.GetDbConnection();
        bool closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            bool hasAgentId;
            await using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA table_info(\"Conversations\");";
                await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
                hasAgentId = false;
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (string.Equals(reader.GetString(1), "AgentId", StringComparison.OrdinalIgnoreCase))
                    {
                        hasAgentId = true;
                        break;
                    }
                }
            }

            if (!hasAgentId)
            {
                await using DbCommand command = connection.CreateCommand();
                command.CommandText =
                    "ALTER TABLE \"Conversations\" " +
                    "ADD COLUMN \"AgentId\" TEXT NOT NULL DEFAULT 'tools';";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
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
