using Android.Util;
using System;

namespace MusicPlayerAvaloniaPortMobile;

/// <summary>
/// The mobile client's log. Everything goes to logcat, which makes it readable with
/// <c>adb logcat -s MusicPlayerMobile</c> from any machine - including a Release build.
/// <para>
/// This exists because the first phone crash was invisible: the crash dialog only offers "send the summary to
/// the OS developers", <see cref="System.Diagnostics.Debug"/> calls are compiled out of Release and
/// <c>Console.WriteLine</c> (which the shared core uses) does not reach logcat in a Release build on Android.
/// Anything a user might need to report - startup stages, why a folder was rejected, why audio failed, and
/// every unhandled exception - has to end up here.
/// </para>
/// </summary>
public static class MobileLog
{
    public const string Tag = "MusicPlayerMobile";

    public static void Info(string message) => Write(LogPriority.Info, message);

    public static void Warn(string message) => Write(LogPriority.Warn, message);

    public static void Error(string message, Exception? exception = null) =>
        Write(LogPriority.Error, exception == null ? message : $"{message}\n{exception}");

    static void Write(LogPriority priority, string message)
    {
        try
        {
            // A single logcat record is limited (about 4k); long exception dumps are split so nothing of the
            // stack trace is lost.
            const int maxChunkLength = 3000;
            for (int offset = 0; offset < message.Length; offset += maxChunkLength)
            {
                string chunk = message.Substring(offset, Math.Min(maxChunkLength, message.Length - offset));
                Log.WriteLine(priority, Tag, chunk);
            }
        }
        catch
        {
            // Logging must never be the reason something fails.
        }
    }
}
