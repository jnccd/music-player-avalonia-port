using System;
using System.IO;

namespace MusicPlayerAvaloniaPort.Helpers.Export;

/// <summary>
/// One song file that can be part of a library export, carrying everything the thresholds of the export
/// library view filter on (see <see cref="LibraryExportSelection"/>). The values are resolved once when
/// the view loads its data - the play chance is expensive to look up and the score/streak/ratio come from
/// the database - while <see cref="SizeBytes"/> is filled in by the background file size measurement of
/// the view.
/// </summary>
public sealed class ExportCandidate(Guid songId, string filePath, float score, int streak, float voteRatio, float playChancePercent)
{
    public Guid SongId { get; } = songId;
    /// <summary>Full path of the song in the music library (the file that gets copied).</summary>
    public string FilePath { get; } = filePath;
    public string Name { get; } = Path.GetFileNameWithoutExtension(filePath);
    public string FileName { get; } = Path.GetFileName(filePath);
    public float Score { get; } = score;
    public int Streak { get; } = streak;
    /// <summary>Likes per dislike, infinite when the song has no dislikes at all (the DxMGP export
    /// treated those as the best possible ratio too, so the ratio threshold never filters them out).</summary>
    public float VoteRatio { get; } = voteRatio;
    /// <summary>Play chance in percent - the same number the statistics view shows. It is 0 for songs
    /// that are not part of the current choosing data structure (e.g. their file is not in the library).</summary>
    public float PlayChancePercent { get; } = playChancePercent;
    /// <summary>Size of the file in bytes, null until the background measurement looked at it (a size
    /// limit can only count songs whose size is known, see <see cref="LibraryExportSelection.SelectSongs"/>).</summary>
    public long? SizeBytes { get; set; }

    // -- Display values for the grid. The columns bind to these and sort by the raw value (the grids of
    // -- this app sort numerically, so a text binding needs DataGridTextColumn.SortMemberPath).
    public string ScoreText => Score.ToString("0.##");
    public string VoteRatioText => float.IsPositiveInfinity(VoteRatio) ? "∞" : VoteRatio.ToString("0.##");
    public string PlayChanceText => PlayChancePercent.ToString("0.###");
    public string SizeText => SizeBytes is long bytes ? FormatSize(bytes) : "";

    public static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / 1024.0 / 1024 / 1024:0.00} GB";
        if (bytes >= 1024L * 1024)
            return $"{bytes / 1024.0 / 1024:0} MB";
        return $"{bytes / 1024.0:0} KB";
    }
}
