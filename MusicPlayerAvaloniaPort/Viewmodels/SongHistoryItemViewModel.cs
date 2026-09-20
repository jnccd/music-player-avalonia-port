using System;

namespace MusicPlayerAvaloniaPort.ViewModels;

/// <summary>
/// One row of the song history view: a listening event (a vote that changed a song's score) with the song
/// name it belongs to. Written once when the view loads, so the values are read-only.
/// </summary>
public sealed class SongHistoryItemViewModel(Guid? songId, string songName, DateTimeOffset date, float scoreChange)
{
    public Guid? SongId { get; } = songId;
    public string SongName { get; } = songName;
    public DateTimeOffset Date { get; } = date;
    public float ScoreChange { get; } = scoreChange;

    public DateTime DateLocal => Date.LocalDateTime;

    /// <summary>History of songs that no longer exist in the database is kept (see
    /// <see cref="MusicPlayerAvaloniaPort.Services.Infrastructure.DbWrapperService.DumpSongHistory"/>) - those
    /// rows have no name and cannot be played.</summary>
    public string NameText => SongName.Length > 0 ? SongName : "(song not in the database)";
    public bool HasSongFile => SongName.Length > 0;

    public string ScoreChangeText => (ScoreChange > 0 ? "+" : "") + ScoreChange.ToString("0.##");
}
