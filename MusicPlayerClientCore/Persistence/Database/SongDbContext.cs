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

            options.UseSqlite($"Data Source={sqlitePath}")
                .AddInterceptors(new SqlitePragmasInterceptor()); // WAL mode, synchronous=NORMAL, busy timeout
            DbStatus = $"Using SQLite DB at {sqlitePath}";
        }
        else
        {
            throw new InvalidOperationException("No valid DB_PROVIDER environment variable set. Use 'sqlite' or 'postgres'.");
        }
    }
}
