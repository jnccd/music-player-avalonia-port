using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Resources;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EzAuth.Interfaces;
using EzAuth.Keycloak;
using MusicPlayerAvaloniaPort.Helpers;
using MusicPlayerAvaloniaPort.Persistence.Configuration;
using MusicPlayerAvaloniaPort.Services.Song;
using MusicPlayerSyncInterface.DTOs;
using MusicPlayerSyncInterface.DTOs.Composites;

namespace MusicPlayerAvaloniaPort.Services.Infrastructure;

enum DownloadType { Song, Video }
record DownloadRequest(string DownloadUrl, DownloadType Type, float? PlaybackProgressSeconds);

[RegisterImplementation(ServiceRegisterType.Singleton, typeof(SongDownloadRequestProcessorService))]
public class SongDownloadRequestProcessorService(SongPlaybackService songPlaybackService)
{
    FileSystemWatcher DownloadFolderWatcher = new();
    readonly List<DownloadRequest> DownloadQueue = [];
    readonly string? TempDownloadFolder = $"{Globals.CurrentExecutablePath}{Path.DirectorySeparatorChar}tmpDownloads";

    Task? SongDownloadRequestQueueProcessorLoopThread = null;
    bool SongDownloadRequestQueueProcessorLoopThreadAborted = false;

    readonly List<string> StateLog = new();
    public string State { get; private set; } = "";

    public void Init()
    {
        if (Config.Data.DownloadFolderPath == null)
        {
            Debug.WriteLine("No download folder path! Aborting SongDownloadRequestProcessorService init...");
            return;
        }

        if (DownloadFolderWatcher != null)
        {
            DownloadFolderWatcher.Dispose();
            DownloadFolderWatcher = new();
        }
        if (SongDownloadRequestQueueProcessorLoopThread != null)
        {
            SongDownloadRequestQueueProcessorLoopThreadAborted = true;
            SongDownloadRequestQueueProcessorLoopThread.Wait();
            SongDownloadRequestQueueProcessorLoopThread.Dispose();
        }

        DownloadFolderWatcher!.Path = Config.Data.DownloadFolderPath;
        DownloadFolderWatcher.Changed += OnDownloadFolderChanged;
        DownloadFolderWatcher.EnableRaisingEvents = true;

        SongDownloadRequestQueueProcessorLoopThread = Task.Run(SongDownloadRequestQueueProcessorLoop);
    }

    private void SongDownloadRequestQueueProcessorLoop()
    {
        Thread.CurrentThread.Name = "SongDownloadRequestQueueProcessor";
        DownloadRequest? request;

        while (true)
        {
            if ((request = DownloadQueue.FirstOrDefault()) != null)
            {
                State = $"Downloading {request.DownloadUrl} as {request.Type}";
                bool success = false;

                if (request.Type == DownloadType.Song)
                    success = DownloadAsSong(request);
                else if (request.Type == DownloadType.Video)
                    success = DownloadAsVideo(request);

                DownloadQueue.Remove(request);
                if (!success)
                    DownloadQueue.Add(request); // Add to the back again to rotate
            }

            Task.Delay(1000).Wait();
            if (SongDownloadRequestQueueProcessorLoopThreadAborted)
                break;
        }
    }

    private void OnDownloadFolderChanged(object source, FileSystemEventArgs ev)
    {
        if (Config.Data.DownloadFolderPath == null)
        {
            Debug.WriteLine("No download folder path! Aborting SongDownloadRequestProcessorService OnDownloadFolderChanged...");
            return;
        }

        string[] filesInDownloadFolder = Directory.GetFiles(Config.Data.DownloadFolderPath);
        foreach (var fileInDownloadFolderPath in filesInDownloadFolder)
        {
            try
            {
                string fileInDownloadFolderName = Path.GetFileName(fileInDownloadFolderPath);
                string fileText = File.ReadAllText(fileInDownloadFolderPath);
                string[] split = fileText.Split('±');

                DownloadType? type = fileInDownloadFolderName switch
                {
                    "MusicPlayer.PlayRequest" => DownloadType.Song,
                    "MusicPlayer.VideoDownloadRequest" => DownloadType.Video,
                    _ => null
                };
                if (type == null)
                    continue;

                DownloadQueue.Add(
                    new(
                        DownloadUrl: split[0],
                        Type: type.Value,
                        PlaybackProgressSeconds: split.Length > 1 ? Convert.ToInt32(split[1].Split('.')[0]) : null
                    ));

                File.Delete(fileInDownloadFolderPath);
            }
            catch (Exception e)
            {
                StateLog.Add(e.ToString());
            }
        }
    }

    // --- Download ---

    private bool DownloadAsSong(DownloadRequest songRequest)
    {
        try
        {
            string downloadTargetFolder = TempDownloadFolder!;
            string download = songRequest.DownloadUrl;
            if (!download.StartsWith("https://"))
                download = $"\"ytsearch: {songRequest.DownloadUrl}\"";

            Console.ForegroundColor = ConsoleColor.Yellow;

            string output = $"-o \"%(title)s.%(ext)s\" -o \"chapter:%(title)s - %(section_title)s.%(ext)s\"";
            if ((download.Contains("ytsearch") ||
                download.Contains("https://www.youtube.com")) && !GetYoutubeVideoTitle(songRequest.DownloadUrl).Contains(" - "))
                output = $"-o \"%(uploader)s - %(title)s.%(ext)s\" -o \"chapter:%(section_title)s.%(ext)s\"";

            // Download Video File
            Process P = new Process();
            P.StartInfo = new ProcessStartInfo("yt-dlp", download + $" -x --audio-format mp3 -P \"{downloadTargetFolder}\" --split-chapters {output} --add-metadata --embed-thumbnail --no-playlist");
            P.StartInfo.UseShellExecute = false;
            P.Start();
            P.WaitForExit();

            // move files to lib
            var downloadedSongs = new List<AvailableSong>();
            var files = Directory.GetFiles(downloadTargetFolder).Where(x => x.EndsWith(".mp3"));
            foreach (string musicFilepath in files)
            {
                string musicFile = Path.GetFileName(musicFilepath);
                string targetPath = $"{Config.Data.SongLibraryPath}\\{musicFile.Replace(" - Topic", "")}";
                // Override
                if (File.Exists(targetPath))
                {
                    Console.WriteLine($"Song override at {targetPath}");
                    File.Delete(targetPath);
                }
                File.Move(musicFilepath, targetPath);

                var availableSong = songPlaybackService.RegisterNewSong(targetPath);

                if (availableSong != null)
                    downloadedSongs.Add(availableSong);
            }

            foreach (var file in Directory.GetFiles(downloadTargetFolder))
                File.Delete(file);

            if (downloadedSongs.FirstOrDefault() == null)
            {
                StateLog.Add("Downloaded file gon :(");
                return false;
            }

            // Play it
            songPlaybackService.PlaySpecificSong(downloadedSongs.First(), secondToStartAt: songRequest.PlaybackProgressSeconds);
        }
        catch (Exception e)
        {
            StateLog.Add(e.ToString());
            return false;
        }

        return true;
    }

    private bool DownloadAsVideo(DownloadRequest videoRequest)
    {
        try
        {
            Process P = new Process();
            P.StartInfo = new ProcessStartInfo("yt-dlp", $"-f mp4 -o \"{Config.Data.DownloadFolderPath}\\%(title)s.%(ext)s\" {videoRequest.DownloadUrl}");
            P.StartInfo.UseShellExecute = false;

            P.Start();
            P.WaitForExit();
        }
        catch (Exception e)
        {
            StateLog.Add(e.ToString());
            return false;
        }

        return true;
    }

    // --- Helpers ---

    public static string GetYoutubeVideoTitle(string search)
    {
        string id = "";
        $"yt-dlp \"ytsearch:{search}\" --get-title --skip-download --no-playlist".RunAsConsoleCommand(10, () => { }, (string o, string err) =>
        {
            id = o.Trim('\n');
        });
        return id;
    }
}