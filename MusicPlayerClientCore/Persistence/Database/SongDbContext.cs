using System;
using Microsoft.EntityFrameworkCore;
using MusicPlayerSyncInterface.DTOs;

namespace MusicPlayerAvaloniaPort.Persistence.Database;

public class SongDbContext : DbContext
{
    public string DbStatus { get; private set; } = "Not connected";
    public DbSet<User> Users { get; set; }
    public DbSet<UpvotedSong> UpvotedSongs { get; set; }
    public DbSet<SongHistoryEntry> SongHistoryEntries { get; set; }

    public DbSet<NotYetSyncedData> NotYetSyncedData { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder) => MusicPlayerSyncInterface.Database.Model.OnModelCreating(modelBuilder);

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        options.EnableSensitiveDataLogging();

        if (Environment.GetEnvironmentVariable("DB_PROVIDER") == "postgres")
        {
            options.UseNpgsql(Environment.GetEnvironmentVariable("POSTGRES_DB_ACCESS"));
            DbStatus = "Using PostgreSQL DB";
        }
        else if (Environment.GetEnvironmentVariable("DB_PROVIDER") == "sqlite" || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DB_PROVIDER")))
        {
            // One data directory for everything the client persists (deleted/cleaned by the user, not by
            // the build): the database used to sit in the build output folder next to the executable.
            var sqlitePath = PersistenceLocations.DatabasePath;

            // Pooling=False is deliberate and load bearing. With pooling on, returning a connection to the
            // pool runs Microsoft.Data.Sqlite's Deactivate(), which un-registers the helper functions EF Core
            // installs (ef_add, regexp, the EF_DECIMAL collation, ...) via sqlite3_create_function(name, null).
            // SQLite answers that with SQLITE_BUSY - "unable to delete/modify user-function due to active
            // statements" - whenever a prepared statement is still active on that connection, which is what
            // happens when a reader was garbage collected but its finalizer has not run yet. The library scan
            // (hundreds of files, several contexts in parallel, heavy read/write churn) hits that window often
            // enough to abort the whole scan, and a failed scan means an empty song list and a player that
            // cannot play anything. It is an upstream Microsoft.Data.Sqlite pool-return behaviour, not lock
            // contention, so busy_timeout cannot help; not pooling removes the step entirely.
            //
            // The cost is that every context opens the file for real and the pragmas below are applied on
            // each open, which is exactly what SqlitePragmasInterceptor is for and is cheap on local storage.
            options.UseSqlite($"Data Source={sqlitePath};Pooling=False")
                .AddInterceptors(new SqlitePragmasInterceptor()); // WAL mode, synchronous=NORMAL, busy timeout
            DbStatus = $"Using SQLite DB at {sqlitePath}";
        }
        else
        {
            throw new InvalidOperationException("No valid DB_PROVIDER environment variable set. Use 'sqlite' or 'postgres'.");
        }
    }
}
