using System;

namespace MusicPlayerAvaloniaPort.Services.Song;

public record AvailableSong(string FilePath, Guid? UpvotedSongId);