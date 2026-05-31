using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using EzAuth.Interfaces;
using EzAuth.Keycloak;
using MusicPlayerAvaloniaPort;
using MusicPlayerAvaloniaPort.Persistence.Configuration;
using MusicPlayerAvaloniaPort.Persistence.Database;
using MusicPlayerSyncInterface.DTOs;

namespace MusicPlayerAvaloniaPort.Services.Song.Sync;

[RegisterImplementation(ServiceRegisterType.Singleton, typeof(UpvotedSongSyncService))]
public class UpvotedSongSyncService
{
    HttpClient _httpClient = new();
    IEzAuthHttpClient? client = null;
    EzAuthAddress? authBackendAddress = null;
    public string State { get => state; private set { OnStateChanged?.Invoke(value); state = value; } }
    private string state = "";
    public Action<string>? OnStateChanged = null;
    JsonSerializerOptions jsonOptions = new() { WriteIndented = true };

    UpvotedSongSyncService()
    {
        Init();
    }

    public void Init(string? password = null, bool TryCallApiInit = false)
    {
        try
        {
            authBackendAddress = GetAuthBackendAddress(Config.Data.SyncServerHost
                ?? throw new Exception($"{nameof(Config.Data.SyncServerHost)} is null!"))
                ?? throw new Exception($"{nameof(GetAuthBackendAddress)} returned null!");
            client = new KeyCloakHttpClient(authBackendAddress, authBackendRefreshToken =>
            {
                Config.Data.AuthBackendRefreshToken = authBackendRefreshToken;
                Config.Save();
            }, Config.Data.AuthBackendRefreshToken, _httpClient);

            if (password != null)
                client.Login(Config.Data.SyncServerUsername ?? throw new Exception($"{nameof(Config.Data.SyncServerUsername)} is null!"), password);
        }
        catch (Exception ex)
        {
            State = $"SyncManager Init failed: {ex.Message}";
            return;
        }

        try
        {
            if (TryCallApiInit)
            {
                using var songDbContext = new SongDbContext();
                var sendObjString = JsonSerializer.Serialize(new UserSongDataAndHistory([], [.. songDbContext.UpvotedSongs], [.. songDbContext.SongHistoryEntries]), jsonOptions);
                var sendContent = new StringContent(sendObjString, Encoding.UTF8, "application/json");
                var res = client.PostAsync($"{Config.Data.SyncServerHost}/sync/init", sendContent).Result;
                State = $"Init {res.StatusCode} {res.Content.ReadAsStringAsync().Result}";
            }
        }
        catch (Exception ex)
        {
            State = $"API Init failed: {ex.Message}";
            return;
        }
    }

    public EzAuthAddress? GetAuthBackendAddress(string syncServerHost)
    {
        var res = _httpClient.GetAsync($"{syncServerHost}/authBackend").Result;
        var content = res.Content.ReadAsStringAsync().Result;
        return JsonSerializer.Deserialize<EzAuthAddress>(content);
    }

    public string GetAccountRegistrationAddress() => client!.GetAccountRegistrationAddress();

    public void Pull()
    {
        try
        {
            var res = client!.GetStringAsync($"{Config.Data.SyncServerHost}/sync/pull").Result;
            var pulledData = JsonSerializer.Deserialize<UserSongDataAndHistory>(res);

            if (pulledData == null)
                throw new Exception("Pulled data was null!");
            if (pulledData.songs.Count() == 0 || pulledData.historyEntries.Count() == 0)
                throw new Exception("Pulled data was empty!");

            Console.WriteLine($"Pulled {pulledData.songs.Count()} songs and {pulledData.historyEntries.Count()} history entries, writing into local db...");

            using var songDbContext = new SongDbContext();
            songDbContext.SongHistoryEntries.RemoveRange(songDbContext.SongHistoryEntries);
            songDbContext.SaveChanges();
            songDbContext.UpvotedSongs.RemoveRange(songDbContext.UpvotedSongs);
            songDbContext.SaveChanges();

            // Add missing user (should just be one, ourselves)
            User pulledUser = pulledData.users.FirstOrDefault() ?? throw new Exception($"pulledData contains no users!");
            if (!songDbContext.Users.Where(x => x.UserId == pulledUser.UserId).Any())
                songDbContext.Users.Add(pulledUser);
            songDbContext.UpvotedSongs.AddRange(pulledData.songs);
            songDbContext.SaveChanges();
            songDbContext.SongHistoryEntries.AddRange(pulledData.historyEntries);
            songDbContext.SaveChanges();
        }
        catch (Exception ex)
        {
            State = $"Pull failed: {ex.Message}";
        }
    }

    public void UploadNewSong(UpvotedSong newSong)
    {
        void SaveUnsyncedData(string newSongjson, string? error = null)
        {
            using var songDbContext = new SongDbContext();
            songDbContext.NotYetSyncedData.Add(new NotYetSyncedData(Guid.NewGuid(), "/sync/new-song", newSongjson, error));
            songDbContext.SaveChanges();
        }

        var newSongjson = JsonSerializer.Serialize(newSong, jsonOptions);
        try
        {
            var newSongContent = new StringContent(newSongjson, Encoding.UTF8, "application/json");
            var res = client!.PostAsync($"{Config.Data.SyncServerHost}/sync/new-song", newSongContent).Result;

            if (!res.IsSuccessStatusCode && res.StatusCode != System.Net.HttpStatusCode.Conflict)
                SaveUnsyncedData(newSongjson, $"{res.IsSuccessStatusCode} {res.Content.ReadAsStringAsync().Result}");

            State = $"UploadNewSong {res.StatusCode} {res.Content.ReadAsStringAsync().Result}";
        }
        catch (Exception ex)
        {
            State = $"UploadNewSong failed: {ex.Message}";

            SaveUnsyncedData(newSongjson, ex.Message);
        }
    }

    public void Vote(SongHistoryEntry newEntry)
    {
        void SaveUnsyncedData(string newEntryjson, string? error = null)
        {
            using var songDbContext = new SongDbContext();
            songDbContext.NotYetSyncedData.Add(new NotYetSyncedData(Guid.NewGuid(), "/sync/vote", newEntryjson, error, newEntry.SongId));
            songDbContext.SaveChanges();
        }

        var newEntryjson = JsonSerializer.Serialize(newEntry, jsonOptions);
        try
        {
            var newEntryContent = new StringContent(newEntryjson, Encoding.UTF8, "application/json");
            var res = client!.PostAsync($"{Config.Data.SyncServerHost}/sync/vote", newEntryContent).Result;

            if (!res.IsSuccessStatusCode && res.StatusCode != System.Net.HttpStatusCode.Conflict)
                SaveUnsyncedData(newEntryjson, $"{res.IsSuccessStatusCode} {res.Content.ReadAsStringAsync().Result}");

            State = $"Vote {res.StatusCode} {res.Content.ReadAsStringAsync().Result}";
        }
        catch (Exception ex)
        {
            State = $"Vote failed: {ex.Message}";

            SaveUnsyncedData(newEntryjson, ex.Message);
        }
    }
}