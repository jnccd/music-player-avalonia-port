using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;

namespace MusicPlayerAvaloniaPort.Persistence.Configuration;

public class ConfigData
{
    // Local Gui Settings
    public int? WindowPositionX { get; set; }
    public int? WindowPositionY { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public Color PrimaryColor { get; set; }
    /// <summary>
    /// When enabled, the player reduces its CPU footprint for devices on a limited power budget
    /// (e.g. laptops on battery): the continuously redrawn visualizations run at a lower frame
    /// rate and the FFT analysis runs at a lower resolution. Playback is not affected.
    /// </summary>
    public bool LowPowerMode { get; set; }

    public string? SongLibraryPath { get; set; }
    /// <summary>
    /// Number of mp3 files the last scan of <see cref="LastScanMp3CountLibraryPath"/> found. The next
    /// scan of that folder uses it as the expected total while it is still enumerating the library, so
    /// its progress can be reported before the walk finished (the real total is only known at the end).
    /// 0 means the current library folder was never scanned on this machine yet (no estimate available).
    /// </summary>
    public int LastScanMp3Count { get; set; }
    /// <summary>
    /// The library folder <see cref="LastScanMp3Count"/> was counted for. The estimate is only used
    /// while the configured library folder still matches this path; switching the folder invalidates
    /// the count until that new folder has been scanned once.
    /// </summary>
    public string? LastScanMp3CountLibraryPath { get; set; }
    public float Volume { get; set; } = 0.8f;

    public string? DownloadFolderPath { get; set; }

    // Sync settings
    public string? AuthBackendRefreshToken { get; set; }
    public string? SyncServerHost { get; set; }
    /// <summary>
    /// This is assumed to be equal to the <see cref="MusicPlayerSyncInterface.DTOs.User.UserId"/> / <see cref="MusicPlayerSyncInterface.DTOs.User.UserHandle"/> field in the User DTO class
    /// </summary>
    public string? SyncServerUsername { get; set; }

    /// <summary>
    /// The incremental history pull cursor per account (<see cref="MusicPlayerSyncInterface.DTOs.User.UserId"/>):
    /// the highest history sequence this client already holds. It is sent as <c>historySince</c> on the
    /// next pull, so the server only sends the entries that are new. Kept out of the song library state
    /// file on purpose - that file is shared between devices through the NAS, while the cursor describes
    /// THIS local database. A missing entry (0) simply means "no cursor yet": the pull then bootstraps
    /// (verifies the newest entries and adopts the cursor) or falls back to the full history.
    /// </summary>
    public Dictionary<string, long> SyncHistorySequences { get; set; } = new();

    public ConfigData()
    {
        PrimaryColor = Color.Parse("#007B82");
    }
}
