using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MusicPlayerAvaloniaPort.Persistence.Database;

/// <summary>
/// Configures performance-relevant SQLite pragmas on every newly opened connection:
/// - journal_mode=WAL: with the default DELETE journal mode, every write transaction (library scan
///   inserts, votes, ...) has to fsync the journal and the database file twice, which makes the
///   thousands of small writes of a scan or of voting take milliseconds each. WAL mode keeps commits
///   cheap and lets readers run while a writer is active. The mode is persistent in the database file,
///   so it only needs to be set once per file (it is idempotent anyway).
/// - synchronous=NORMAL: in WAL mode this avoids an extra fsync per commit while still being safe
///   against database corruption (only the very last commit of a crash can be lost).
/// - busy_timeout: when multiple connections write at the same time (e.g. the parallel library scan),
///   writers wait for the lock up to this timeout instead of failing immediately with "database is
///   locked".
/// </summary>
public class SqlitePragmasInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ApplyPragmas(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        ApplyPragmas(connection);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    static void ApplyPragmas(DbConnection connection)
    {
        if (connection is not SqliteConnection)
            return; // Only relevant for the SQLite provider

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            // Non-fatal: without the tuning the database still works, only slower to write.
            Console.WriteLine($"Could not configure SQLite pragmas: {ex.Message}");
        }
    }
}
