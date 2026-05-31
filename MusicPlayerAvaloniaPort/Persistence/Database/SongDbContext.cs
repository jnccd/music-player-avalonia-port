using System;
using System.IO;
using System.Reflection;
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
            var exePath = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location ?? "~") + Path.DirectorySeparatorChar;
            var sqlitePath = (Environment.GetEnvironmentVariable("MUSIC_PLAYER_SQLITE_DB_PATH") ?? exePath) + "song.db";

            options.UseSqlite($"Data Source={sqlitePath}");
            DbStatus = $"Using SQLite DB at {sqlitePath}";
        }
        else
        {
            throw new InvalidOperationException("No valid DB_PROVIDER environment variable set. Use 'sqlite' or 'postgres'.");
        }
    }
}
