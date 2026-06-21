using System;
using System.Diagnostics;
using System.Threading.Tasks;
using SoundFlow.Enums;
using Tmds2.DBus.Protocol;

namespace MusicPlayerAvaloniaPort.Services.Infrastructure;

public class MprisService(AudioLibWrapperService audioLibWrapperService, SongPlaybackService songPlaybackService, DbWrapperService dbWrapperService)
{
    public void Init()
    {
        Task.Run(RunMprisServiceAsync);
    }

    private async Task RunMprisServiceAsync()
    {
        while (true)
        {
            using var connection = new DBusConnection(DBusAddress.Session!);
            await connection.ConnectAsync();

            using var handler = new MprisHandler(connection,
            GetPlayerStatus: () =>
            {
                var currentSong = dbWrapperService.GetContext().GetUpvotedSongById(songPlaybackService.CurrentlyPlaying?.UpvotedSongId);
                return new PlayerStatus(
                    Identity: "MusicPlayerAvaloniaPort",
                    DesktopEntry: "music-player-avalonia-port",
                    CurrentSongTitle: currentSong?.Name ?? string.Empty,
                    CurrentSongArtist: currentSong?.Artist ?? string.Empty,
                    CurrentSongAlbum: currentSong?.Album ?? string.Empty,
                    CurrentSongPosition: TimeSpan.FromSeconds((audioLibWrapperService.SongDurationSeconds ?? 0) * (audioLibWrapperService.PlayProgress ?? 0)),
                    CurrentSongLength: TimeSpan.FromSeconds(audioLibWrapperService.SongDurationSeconds ?? 0),
                    Volume: audioLibWrapperService.Volume,
                    PlaybackStatus: audioLibWrapperService.PlayState switch
                    {
                        PlaybackState.Playing => PlaybackStatus.Playing,
                        PlaybackState.Paused => PlaybackStatus.Paused,
                        PlaybackState.Stopped => PlaybackStatus.Stopped,
                        null => PlaybackStatus.Stopped,
                        _ => throw new InvalidOperationException("Unknown play state")
                    }
                );
            }, HandleMprisEvent: (mprisEvent) =>
            {
                switch (mprisEvent.Type)
                {
                    case MprisEventType.PlayPause:
                        audioLibWrapperService.TogglePlayPause();
                        break;
                    case MprisEventType.Play:
                        if (audioLibWrapperService.PlayState != PlaybackState.Playing)
                            audioLibWrapperService.TogglePlayPause();
                        break;
                    case MprisEventType.Pause:
                        if (audioLibWrapperService.PlayState != PlaybackState.Paused)
                            audioLibWrapperService.TogglePlayPause();
                        break;
                    case MprisEventType.Stop:
                        songPlaybackService.UpvoteLockedIn = !songPlaybackService.UpvoteLockedIn;
                        break;
                    case MprisEventType.Next:
                        songPlaybackService.GetNextSong();
                        break;
                    case MprisEventType.Previous:
                        songPlaybackService.GetPreviousSong();
                        break;
                    case MprisEventType.Seek:
                        audioLibWrapperService.PlayProgress = (float)((mprisEvent.Position?.TotalSeconds ?? 0) / (audioLibWrapperService.SongDurationSeconds ?? 1));
                        break;
                    case MprisEventType.SetPosition:
                        audioLibWrapperService.PlayProgress = (float)((mprisEvent.Position?.TotalSeconds ?? 0) / (audioLibWrapperService.SongDurationSeconds ?? 1));
                        break;
                    case MprisEventType.SetVolume:
                        audioLibWrapperService.Volume = (float)(mprisEvent.Volume ?? 0);
                        break;
                }
            });

            connection.AddMethodHandler(handler);

            await connection.RequestNameAsync(
                "org.mpris.MediaPlayer2.MusicPlayerAvaloniaPort",
                RequestNameOptions.ReplaceExisting);

            Console.WriteLine("MPRIS service running...");
            Debug.WriteLine("MPRIS service running...");
            Exception? reason = await connection.DisconnectedAsync();
            Console.WriteLine($"Connection lost: {reason}. Reconnecting...");
            Debug.WriteLine($"Connection lost: {reason}. Reconnecting...");
        }
    }
}