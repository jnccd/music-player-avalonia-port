using System;

namespace MusicPlayerAvaloniaPort.Services.Infrastructure;

/// <summary>
/// The audio playback surface the platform independent song services (playback, voting, volume
/// normalization) rely on.
/// <para>
/// Everything that is genuinely platform specific lives behind this interface: the desktop client
/// implements it with the SoundFlow based <c>AudioLibWrapperService</c> (which additionally owns the FFT
/// analysis machinery the diagram view needs), while the Android client implements it with a lean player
/// that deliberately does <b>not</b> do any spectrum analysis - on a phone the FFT costs CPU/battery for
/// something no mobile music player UI shows.
/// </para>
/// <para>
/// Keeping this interface free of SoundFlow types is intentional: the shared core then does not depend on
/// the audio backend at all, so the mobile client can pick its own playback strategy (or even a completely
/// different backend) without touching the shared services.
/// </para>
/// </summary>
public interface IAudioPlaybackService
{
    /// <summary>Playback volume in [0,1]. The song services write the (normalized) volume here.</summary>
    float Volume { get; set; }

    /// <summary>
    /// Playback position in [0,1], or null while nothing is loaded. Setting it seeks; seeking is tracked
    /// separately in <see cref="SeekedPlayProgress"/> because the vote scoring needs to distinguish
    /// "listened through" from "skipped/sought".
    /// </summary>
    float? PlayProgress { get; set; }

    /// <summary>Duration of the loaded song in seconds, or null while nothing is loaded.</summary>
    float? SongDurationSeconds { get; }

    /// <summary>
    /// Signed amount of progress that was seeked instead of played (positive forward, negative backward).
    /// Always 0 for the current song when nothing was seeked.
    /// </summary>
    float SeekedPlayProgress { get; }

    /// <summary>
    /// Raised when the currently playing song reached its end. The playback service reacts by switching to
    /// the next song, so implementations must raise it on a thread that is safe for that (the desktop
    /// implementation hops onto a task pool thread).
    /// </summary>
    event EventHandler<EventArgs>? PlaybackEnded;

    /// <summary>
    /// Raised once the implementation finished reading the whole song's samples. Only implementations that
    /// pre-read a song to measure it (see <see cref="PlaySong"/>) raise this; it is the trigger for the
    /// volume normalization to store <see cref="CurrentSongRootMeanSquare"/>.
    /// </summary>
    event EventHandler<EventArgs>? FinishedReading;

    /// <summary>
    /// Starts playing the given song file.
    /// </summary>
    /// <param name="songPath">Absolute path of the song file in the local song library.</param>
    /// <param name="measureWholeSongForVolumeNormalization">
    /// True when the local database has no volume measurement for this song yet, so the implementation
    /// should read the whole song to be able to report <see cref="CurrentSongRootMeanSquare"/> (and raise
    /// <see cref="FinishedReading"/> when it is done). When false the song was already measured and the
    /// implementation may use its cheaper playback path. Implementations are free to ignore the hint -
    /// mobile, for example, always decodes on the fly, but it still measures the song in the background.
    /// </param>
    void PlaySong(string songPath, bool measureWholeSongForVolumeNormalization);

    /// <summary>
    /// Toggles between playing and paused. A no-op when no song is loaded.
    /// </summary>
    /// <param name="updateAudioDevicesInfo">
    /// Desktop only: re-read the OS audio devices first (used when the default device may have changed).
    /// </param>
    void TogglePlayPause(bool updateAudioDevicesInfo = false);

    /// <summary>
    /// The measured loudness (root mean square of the decoded samples) of the song that finished being
    /// read, or null while no measurement is available. The volume service stores it on the song row and
    /// later uses it to normalize the playback volume of that song.
    /// </summary>
    float? CurrentSongRootMeanSquare { get; }

    /// <summary>
    /// The song file <see cref="CurrentSongRootMeanSquare"/> belongs to, or null while there is no
    /// measurement. Reading a file to measure it takes long enough that the user may have skipped to the
    /// next song already, so the volume service compares this with the currently playing song before
    /// storing the value - otherwise the loudness of a skipped song would be written onto its successor.
    /// </summary>
    string? MeasuredSongPath { get; }
}
