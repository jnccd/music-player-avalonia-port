using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using IOPath = System.IO.Path;
using Android.Media;
using Android.Media.Session;
using Android.OS;
using MusicPlayerAvaloniaPort;
using MusicPlayerAvaloniaPort.Services.Song;
using MusicPlayerAvaloniaPortMobile.Views;
using System;
using System.IO;

namespace MusicPlayerAvaloniaPortMobile.Services;

/// <summary>
/// The ongoing media notification every Android music player has: current song, cover art and
/// previous / play-pause / next controls, shown in the notification shade and on the lock screen.
/// <para>
/// It is a foreground service (type <c>mediaPlayback</c>) rather than a plain notification, for two
/// reasons: Android only lets a notification be a persistent media notification when a foreground service
/// owns it, and a foreground service is what keeps the process - and therefore playback - alive when the
/// user leaves the app. That also closes the "playback is not a background service" gap noted in the
/// README.
/// </para>
/// <para>
/// The notification deliberately drives the SAME shared services the UI drives
/// (<see cref="SongPlaybackService"/>, <see cref="MobileAudioPlayerService"/>) instead of keeping its own
/// player state, so skipping from the notification runs the identical vote logic
/// (<c>GetNextSong</c>) as skipping in the app.
/// </para>
/// <para>
/// Built on the platform APIs only (<see cref="Notification.MediaStyle"/>,
/// <see cref="MediaSession"/>) - no AndroidX Media dependency, so nothing new has to be trimmed/AOT
/// analysed.
/// </para>
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public class MobilePlaybackNotificationService : Service
{
    /// <summary>Notification actions; the service receives them through <see cref="PendingIntent"/>s.</summary>
    public const string ActionPlayPause = "com.jnccd.musicplayermobile.action.PLAY_PAUSE";
    public const string ActionNext = "com.jnccd.musicplayermobile.action.NEXT";
    public const string ActionPrevious = "com.jnccd.musicplayermobile.action.PREVIOUS";
    public const string ActionStop = "com.jnccd.musicplayermobile.action.STOP";

    const int NotificationId = 4711;
    const string ChannelId = "playback";
    const string SessionTag = "MusicPlayerMobile";

    NotificationManager? notificationManager;
    MediaSession? mediaSession;
    SongPlaybackService? playback;
    MobileAudioPlayerService? audio;
    SongInfoService? songInfo;

    bool subscribed;
    bool isForeground;
    Bitmap? largeIcon;

    /// <summary>Song and duration the media session metadata was published for, so the once-per-second
    /// refresh does not rebuild it needlessly.</summary>
    Guid? metadataSongId;
    long metadataDurationMs = -1;

    Handler? refreshHandler;
    bool refreshScheduled;

    /// <summary>
    /// Starts (or leaves running) the playback notification. Must be called while the app is in the
    /// foreground: Android refuses to start a foreground service from the background
    /// (<c>ForegroundServiceStartNotAllowedException</c>), which is why this is wrapped and logged instead
    /// of thrown.
    /// </summary>
    public static void Start(Context? context)
    {
        context ??= MobilePlatform.ApplicationContext;
        if (context == null)
            return;

        try
        {
            var intent = new Intent(context, typeof(MobilePlaybackNotificationService));
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                context.StartForegroundService(intent);
            else
                context.StartService(intent);
        }
        catch (Exception ex)
        {
            MobileLog.Warn($"Could not start the playback notification service: {ex.Message}");
        }
    }

    /// <summary>Removes the notification and stops the foreground service.</summary>
    public static void Stop(Context? context)
    {
        context ??= MobilePlatform.ApplicationContext;
        if (context == null)
            return;

        try
        {
            context.StopService(new Intent(context, typeof(MobilePlaybackNotificationService)));
        }
        catch (Exception ex)
        {
            MobileLog.Warn($"Could not stop the playback notification service: {ex.Message}");
        }
    }

    public override void OnCreate()
    {
        base.OnCreate();

        notificationManager = GetSystemService(NotificationService) as NotificationManager;
        CreateChannel();
        CreateSession();
        ScheduleSessionRefresh();

        try
        {
            playback = ServiceContainer.GetService<SongPlaybackService>();
            audio = ServiceContainer.GetService<MobileAudioPlayerService>();
            songInfo = ServiceContainer.GetService<SongInfoService>();

            playback.NewSongStarted += OnNewSongStarted;
            audio.PlaybackStateChanged += OnPlaybackStateChanged;
            subscribed = true;
        }
        catch (Exception ex)
        {
            // A notification without a player is useless, but it must not take the app down: the service
            // simply shows what it knows and the UI keeps working.
            MobileLog.Error("The playback notification could not be wired to the player", ex);
        }
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        HandleAction(intent?.Action);

        if (!isForeground)
        {
            if (!ShowForeground())
                return StartCommandResult.NotSticky; // Nothing to show (e.g. no song yet) - do not linger
        }
        else
        {
            UpdateNotification();
        }

        // Not sticky: if the process is killed, playback is gone too, and a restarted service would only
        // resurrect a notification for a player that no longer exists.
        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        if (subscribed)
        {
            if (playback != null)
                playback.NewSongStarted -= OnNewSongStarted;
            if (audio != null)
                audio.PlaybackStateChanged -= OnPlaybackStateChanged;
            subscribed = false;
        }

        refreshHandler?.RemoveCallbacksAndMessages(null);
        refreshHandler?.Dispose();
        refreshHandler = null;

        mediaSession?.Release();
        mediaSession?.Dispose();
        mediaSession = null;
        largeIcon?.Dispose();
        largeIcon = null;

        base.OnDestroy();
    }

    public override IBinder? OnBind(Intent? intent) => null;

    // ---------- actions ----------

    void HandleAction(string? action)
    {
        if (string.IsNullOrEmpty(action))
            return;

        try
        {
            switch (action)
            {
                case ActionPlayPause:
                    audio?.TogglePlayPause();
                    break;
                case ActionNext:
                    if (playback?.CurrentlyPlaying != null)
                        playback.GetNextSong();
                    break;
                case ActionPrevious:
                    if (playback?.CurrentlyPlaying != null)
                        playback.GetPreviousSong();
                    break;
                case ActionStop:
                    StopForeground(StopForegroundFlags.Remove);
                    StopSelf();
                    break;
            }

            MobileLog.Info($"Playback notification action: {action}");
        }
        catch (Exception ex)
        {
            MobileLog.Error($"Playback notification action \"{action}\" failed", ex);
        }
    }

    void OnNewSongStarted(object? sender, AvailableSong song)
    {
        MobileLog.Info($"Playback notification: now playing \"{IOPath.GetFileName(song.FilePath)}\"");
        UpdateNotification();
    }

    void OnPlaybackStateChanged(object? sender, SoundFlow.Enums.PlaybackState state) => UpdateNotification();

    /// <summary>Keeps the media session's transport state in sync, so the lock screen and bluetooth
    /// controls show the right button and can drive playback.</summary>
    public void PublishSessionState()
    {
        if (mediaSession == null)
            return;

        try
        {
            bool isPlaying = audio?.PlayState == SoundFlow.Enums.PlaybackState.Playing;
            double durationSeconds = audio?.SongDurationSeconds ?? 0;
            double positionSeconds = (audio?.PlayProgress ?? 0) * durationSeconds;

            // The transport actions the session advertises are a long bitmask of PlaybackState.Action*
            // constants (not an enum) - this is what makes the lock screen / bluetooth buttons appear.
            var builder = new Android.Media.Session.PlaybackState.Builder()
                .SetActions(PlaybackState.ActionPlay | PlaybackState.ActionPause | PlaybackState.ActionStop
                    | PlaybackState.ActionSkipToNext | PlaybackState.ActionSkipToPrevious)
                .SetState(
                    isPlaying ? PlaybackStateCode.Playing : PlaybackStateCode.Paused,
                    (long)(positionSeconds * 1000),
                    1.0f);

            mediaSession.SetPlaybackState(builder.Build());

            var song = playback?.CurrentlyPlaying;
            if (song == null)
                return;

            // The duration lives in the METADATA, not in the playback state: without it the notification's
            // progress row shows 00:00 / 00:00 however correct the position is. This is also what feeds the
            // lock screen and bluetooth metadata.
            //
            // It is published only when the song or the duration changed, because right after starting a song
            // the decoder has not reported the duration yet - the periodic refresh comes back with it a moment
            // later, and without that second publish the row would stay at 00:00 for the whole song.
            long durationMs = (long)(durationSeconds * 1000);
            if (metadataSongId == song.UpvotedSongId && metadataDurationMs == durationMs)
                return;

            var metadata = new MediaMetadata.Builder()
                .PutString(MediaMetadata.MetadataKeyTitle, IOPath.GetFileNameWithoutExtension(song.FilePath))
                .PutString(MediaMetadata.MetadataKeyArtist, BuildArtistText(song))
                .PutLong(MediaMetadata.MetadataKeyDuration, durationMs);

            if (largeIcon != null)
                metadata.PutBitmap(MediaMetadata.MetadataKeyAlbumArt, largeIcon);

            mediaSession.SetMetadata(metadata.Build());
            metadataSongId = song.UpvotedSongId;
            metadataDurationMs = durationMs;
        }
        catch (Exception ex)
        {
            MobileLog.Warn($"Could not publish the media session state: {ex.Message}");
        }
    }

    /// <summary>
    /// Re-publishes the media session state once per second while the service lives. Mostly this catches the
    /// song duration (unknown for the first moment of a song) and seeks made from the app; the notification's
    /// progress bar itself is extrapolated by the system from the last published state.
    /// </summary>
    void ScheduleSessionRefresh()
    {
        refreshHandler ??= new Handler(Looper.MainLooper!);
        if (refreshScheduled)
            return;

        refreshScheduled = true;
        refreshHandler.PostDelayed(() =>
        {
            refreshScheduled = false;
            if (mediaSession == null)
                return;

            PublishSessionState();
            ScheduleSessionRefresh();
        }, 1000);
    }

    /// <summary>The artist/album line of the session metadata, read from the song's database row.</summary>
    string BuildArtistText(AvailableSong song)
    {
        const string fallback = "Music Player";

        try
        {
            using var context = ServiceContainer
                .GetService<MusicPlayerAvaloniaPort.Services.Infrastructure.DbWrapperService>()
                .GetContext();
            var row = context.GetUpvotedSongByIdOrNull(song.UpvotedSongId);
            if (row == null)
                return fallback;

            bool hasArtist = !string.IsNullOrWhiteSpace(row.Artist);
            bool hasAlbum = !string.IsNullOrWhiteSpace(row.Album);
            if (hasArtist && hasAlbum)
                return $"{row.Artist} — {row.Album}";
            if (hasArtist)
                return row.Artist;
            if (hasAlbum)
                return row.Album;
        }
        catch (Exception ex)
        {
            MobileLog.Warn($"Could not read the artist of the playing song: {ex.Message}");
        }

        return fallback;
    }

    void CreateSession()
    {
        try
        {
            mediaSession = new MediaSession(this, SessionTag);
            mediaSession.SetFlags(MediaSessionFlags.HandlesMediaButtons | MediaSessionFlags.HandlesTransportControls);
            mediaSession.SetCallback(new SessionCallback(this));
            mediaSession.Active = true;
        }
        catch (Exception ex)
        {
            MobileLog.Warn($"Could not create the media session: {ex.Message}");
        }
    }

    void CreateChannel()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26) || notificationManager == null)
            return;

        try
        {
            var channel = new NotificationChannel(ChannelId, "Playback", NotificationImportance.Low)
            {
                Description = "Shows the song that is playing and its controls."
            };
            channel.SetShowBadge(false);
            channel.LockscreenVisibility = NotificationVisibility.Public;
            notificationManager.CreateNotificationChannel(channel);
        }
        catch (Exception ex)
        {
            MobileLog.Warn($"Could not create the playback notification channel: {ex.Message}");
        }
    }

    // ---------- notification ----------

    bool ShowForeground()
    {
        var notification = BuildNotification();
        if (notification == null)
            return false;

        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(34))
                StartForeground(NotificationId, notification, ForegroundService.TypeMediaPlayback);
            else
                StartForeground(NotificationId, notification);

            isForeground = true;
            return true;
        }
        catch (Exception ex)
        {
            MobileLog.Error("Could not show the playback notification", ex);
            return false;
        }
    }

    void UpdateNotification()
    {
        if (notificationManager == null)
            return;

        try
        {
            PublishSessionState();

            var notification = BuildNotification();
            if (notification == null)
                return;

            if (isForeground)
                notificationManager.Notify(NotificationId, notification);
            else
                ShowForeground();
        }
        catch (Exception ex)
        {
            MobileLog.Warn($"Could not update the playback notification: {ex.Message}");
        }
    }

    Notification? BuildNotification()
    {
        var song = playback?.CurrentlyPlaying;
        if (song == null)
            return null;

        bool isPlaying = audio?.PlayState == SoundFlow.Enums.PlaybackState.Playing;
        string title = IOPath.GetFileNameWithoutExtension(song.FilePath);
        string subtitle = BuildArtistText(song);

        try
        {
            Notification.Builder builder = OperatingSystem.IsAndroidVersionAtLeast(26)
                ? new Notification.Builder(this, ChannelId)
                : new Notification.Builder(this);

            SetLargeIcon(ref largeIcon, builder, song);

            builder
                .SetContentTitle(title)
                .SetContentText(subtitle)
                .SetSmallIcon(Resource.Drawable.ic_stat_music)
                .SetContentIntent(OpenAppIntent())
                .SetDeleteIntent(ServiceIntent(ActionStop, 4))
                .SetOngoing(isPlaying)
                .SetOnlyAlertOnce(true)
                .SetShowWhen(false)
                .SetVisibility(NotificationVisibility.Public)
                .SetCategory(Notification.CategoryTransport)
                .AddAction(ServiceAction(Resource.Drawable.ic_media_prev, "Previous", ActionPrevious, 1))
                .AddAction(ServiceAction(isPlaying ? Resource.Drawable.ic_media_pause : Resource.Drawable.ic_media_play,
                    isPlaying ? "Pause" : "Play", ActionPlayPause, 2))
                .AddAction(ServiceAction(Resource.Drawable.ic_media_next, "Next", ActionNext, 3));

            // MediaStyle is what makes the system treat this as a media notification (compact view, lock
            // screen, media output switcher, bluetooth metadata) instead of a plain one.
            var style = new Notification.MediaStyle()
                .SetShowActionsInCompactView(0, 1, 2);
            if (mediaSession?.SessionToken != null)
                style.SetMediaSession(mediaSession.SessionToken);
            builder.SetStyle(style);

            return builder.Build();
        }
        catch (Exception ex)
        {
            MobileLog.Error("Could not build the playback notification", ex);
            return null;
        }
    }

    /// <summary>
    /// Puts the song's embedded cover art on the notification. The bitmap is kept in a field and the previous
    /// one is only disposed when it is replaced - the notification reads it asynchronously while the shade
    /// renders.
    /// </summary>
    void SetLargeIcon(ref Bitmap? current, Notification.Builder builder, AvailableSong song)
    {
        try
        {
            byte[]? art = songInfo?.GetCoverArtBytesOfSong(song);
            if (art == null || art.Length == 0)
                return;

            using var stream = new MemoryStream(art);
            var bitmap = BitmapFactory.DecodeStream(stream);
            if (bitmap == null)
                return;

            current?.Dispose();
            current = bitmap;
            builder.SetLargeIcon(bitmap);
        }
        catch (Exception ex)
        {
            MobileLog.Warn($"Could not load the cover art for the notification: {ex.Message}");
        }
    }

    Notification.Action ServiceAction(int icon, string title, string action, int requestCode) =>
        new Notification.Action.Builder(icon, new Java.Lang.String(title), ServiceIntent(action, requestCode)).Build();

    PendingIntent ServiceIntent(string action, int requestCode)
    {
        var intent = new Intent(this, typeof(MobilePlaybackNotificationService)).SetAction(action);
        return PendingIntent.GetService(this, requestCode, intent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    }

    PendingIntent OpenAppIntent()
    {
        var intent = new Intent(this, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.SingleTop);
        return PendingIntent.GetActivity(this, 0, intent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    }

    /// <summary>Forwards the lock screen / bluetooth / headset transport buttons to the same actions the
    /// notification buttons use.</summary>
    sealed class SessionCallback(MobilePlaybackNotificationService service) : MediaSession.Callback
    {
        public override void OnPlay() => service.HandleAction(ActionPlayPause);

        public override void OnPause() => service.HandleAction(ActionPlayPause);

        public override void OnSkipToNext() => service.HandleAction(ActionNext);

        public override void OnSkipToPrevious() => service.HandleAction(ActionPrevious);

        public override void OnStop() => service.HandleAction(ActionStop);
    }
}
