using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dbus.Mpris;
using Tmds2.DBus.Protocol;

public enum PlaybackStatus
{
    Playing,
    Paused,
    Stopped
}

public record PlayerStatus(string Identity, string DesktopEntry, string CurrentSongTitle, string CurrentSongArtist, string CurrentSongAlbum, TimeSpan CurrentSongPosition, TimeSpan CurrentSongLength, double Volume, PlaybackStatus PlaybackStatus);

public enum MprisEventType
{
    Play,
    PlayPause,
    Pause,
    Stop,
    Next,
    Previous,
    Seek,
    SetPosition,
    SetVolume
}

public record MprisEvent(MprisEventType Type, TimeSpan? Position = null, double? Volume = null);

internal class MprisHandler : DBusHandler,
                       IMediaPlayer2Handler,
                       IMediaPlayer2Properties,
                       IPlayerHandler,
                       IPlayerProperties,
                       IDisposable
{
    DBusConnection Connection;
    Func<PlayerStatus> GetPlayerStatus;
    Action<MprisEvent> HandleMprisEvent;

    PlayerStatus? PlayerStatusCache
    {
        get
        {
            if (field == null || PlayerStatusCacheLastUpdated + PlayerStatusCacheDuration < DateTime.Now)
            {
                field = GetPlayerStatus();
                PlayerStatusCacheLastUpdated = DateTime.Now;
            }
            return field;
        }
    } = null;
    DateTime PlayerStatusCacheLastUpdated { get; set; } = DateTime.MinValue;
    TimeSpan PlayerStatusCacheDuration { get; set; } = TimeSpan.FromSeconds(1);

    public MprisHandler(DBusConnection connection, Func<PlayerStatus> GetPlayerStatus, Action<MprisEvent> HandleMprisEvent)
        : base(connection, path: "/org/mpris/MediaPlayer2", handlesChildPaths: true)
    {
        Connection = connection;

        this.GetPlayerStatus = GetPlayerStatus;
        this.HandleMprisEvent = HandleMprisEvent;

        //EmitAllProperties();
    }

    public void EmitAllProperties()
    {
        Connection.EmitPropertyChanged(Path, this,
            PlayerProperty.PlaybackStatus);
        Connection.EmitPropertyChanged(Path, this,
            PlayerProperty.Metadata);
        Connection.EmitPropertyChanged(Path, this,
            PlayerProperty.Volume);
        Connection.EmitPropertyChanged(Path, this,
            PlayerProperty.Position);
        Connection.EmitPropertyChanged(Path, this,
            PlayerProperty.MinimumRate);
        Connection.EmitPropertyChanged(Path, this,
            PlayerProperty.MaximumRate);
        Connection.EmitPropertyChanged(Path, this,
            PlayerProperty.CanGoNext);
        Connection.EmitPropertyChanged(Path, this,
            PlayerProperty.CanGoPrevious);
        Connection.EmitPropertyChanged(Path, this,
            PlayerProperty.CanPlay);
        Connection.EmitPropertyChanged(Path, this,
            PlayerProperty.CanPause);
        Connection.EmitPropertyChanged(Path, this,
            PlayerProperty.CanSeek);
        Connection.EmitPropertyChanged(Path, this,
            PlayerProperty.CanControl);
    }

    private void EmitProperty(PlayerProperty property)
        => Connection.EmitPropertyChanged(Path, this, property);

    // === IMediaPlayer2Handler (methods) ===
    public ValueTask QuitAsync()
    {
        return default;
    }

    public ValueTask RaiseAsync() => default; // No-op

    // === IPlayerHandler (methods) ===
    public ValueTask NextAsync()
    {
        HandleMprisEvent(new MprisEvent(MprisEventType.Next));
        Console.WriteLine("MprisHandler Next");
        return default;
    }

    public ValueTask PreviousAsync()
    {
        HandleMprisEvent(new MprisEvent(MprisEventType.Previous));
        Console.WriteLine("MprisHandler Previous");
        return default;
    }

    public ValueTask PauseAsync()
    {
        HandleMprisEvent(new MprisEvent(MprisEventType.Pause));
        Console.WriteLine("MprisHandler Pause");
        EmitProperty(PlayerProperty.PlaybackStatus);
        return default;
    }

    public ValueTask PlayPauseAsync()
    {
        HandleMprisEvent(new MprisEvent(MprisEventType.PlayPause));
        Console.WriteLine("MprisHandler PlayPause");
        EmitProperty(PlayerProperty.PlaybackStatus);
        return default;
    }

    public ValueTask StopAsync()
    {
        HandleMprisEvent(new MprisEvent(MprisEventType.Stop));
        Console.WriteLine("MprisHandler Stop (or like)");
        EmitProperty(PlayerProperty.PlaybackStatus);
        return default;
    }

    public ValueTask PlayAsync()
    {
        HandleMprisEvent(new MprisEvent(MprisEventType.Play));
        Console.WriteLine("MprisHandler Play");
        EmitProperty(PlayerProperty.PlaybackStatus);
        return default;
    }

    public ValueTask SeekAsync(long offset)
    {
        HandleMprisEvent(new MprisEvent(MprisEventType.Seek, TimeSpan.FromMicroseconds(offset)));
        Console.WriteLine($"MprisHandler Seek: {offset}");
        return default;
    }

    public ValueTask SetPositionAsync(ObjectPath trackId, long position)
    {
        HandleMprisEvent(new MprisEvent(MprisEventType.SetPosition, TimeSpan.FromMicroseconds(position)));
        Console.WriteLine($"MprisHandler SetPosition: {trackId}, {position}");
        return default;
    }

    public ValueTask OpenUriAsync(string uri)
    {
        Console.WriteLine($"MprisHandler OpenUri: {uri}");
        return default;
    }

    // === IMediaPlayer2Properties ===
    public string Identity => PlayerStatusCache!.Identity;
    public string DesktopEntry => PlayerStatusCache!.DesktopEntry;
    public bool CanQuit => false;
    public bool CanRaise => false;
    public bool HasTrackList => false;
    public string[] SupportedUriSchemes => new[] { "file", "http" };
    public string[] SupportedMimeTypes => new[] { "audio/mpeg", "audio/flac" };

    // === IPlayerProperties ===

    public string PlaybackStatus => PlayerStatusCache!.PlaybackStatus.ToString();
    public string LoopStatus { get; set; } = "None";
    public double Rate { get; set; } = 1.0;
    public bool Shuffle { get; set; } = false;

    public Dictionary<string, VariantValue> Metadata => new()
    {
        // ["mpris:trackid"] = new ObjectPath("/org/mpris/MediaPlayer2/TrackList/1"),
        ["xesam:title"] = PlayerStatusCache!.CurrentSongTitle,
        ["xesam:artist"] = VariantValue.Array(new string[] { string.IsNullOrWhiteSpace(PlayerStatusCache!.CurrentSongArtist) ? "Unknown Artist" : PlayerStatusCache.CurrentSongArtist }),
        ["xesam:album"] = string.IsNullOrWhiteSpace(PlayerStatusCache!.CurrentSongAlbum) ? "Unknown Album" : PlayerStatusCache.CurrentSongAlbum,
        ["mpris:length"] = (long)PlayerStatusCache!.CurrentSongLength.TotalMicroseconds
    };

    public double Volume
    {
        get => PlayerStatusCache!.Volume;
        set => HandleMprisEvent(new(MprisEventType.SetVolume, Volume: value));
    }
    public long Position => (long)PlayerStatusCache!.CurrentSongPosition.TotalMicroseconds;
    public double MinimumRate => 1.0;
    public double MaximumRate => 1.0;
    public bool CanGoNext => true;
    public bool CanGoPrevious => true;
    public bool CanPlay => true;
    public bool CanPause => true;
    public bool CanSeek => true;
    public bool CanControl => true;

    public bool Fullscreen { get; set; } = false;

    public bool CanSetFullscreen => false;

    Dictionary<string, Tmds2.DBus.Protocol.VariantValue> IPlayerProperties.Metadata => Metadata;

    public void Dispose() { }

    // For MediaPlayer2
    public ValueTask HandleGetPropertyAsync(IMediaPlayer2Handler.GetPropertyContext context)
        => context.Handle(this);

    public ValueTask HandleGetAllPropertiesAsync(IMediaPlayer2Handler.GetAllPropertiesContext context)
        => context.Handle(this);

    public ValueTask HandleSetPropertyAsync(IMediaPlayer2Handler.SetPropertyContext context)
        => context.Handle(this);

    // For Player
    public ValueTask HandleGetPropertyAsync(IPlayerHandler.GetPropertyContext context)
        => context.Handle(this);

    public ValueTask HandleGetAllPropertiesAsync(IPlayerHandler.GetAllPropertiesContext context)
        => context.Handle(this);

    public ValueTask HandleSetPropertyAsync(IPlayerHandler.SetPropertyContext context)
        => context.Handle(this);
}