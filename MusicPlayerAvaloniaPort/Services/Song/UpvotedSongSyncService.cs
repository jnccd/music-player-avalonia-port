using System;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using EzAuth.Interfaces;
using EzAuth.Keycloak;
using MusicPlayerAvaloniaPort.Persistence.Configuration;
using MusicPlayerAvaloniaPort.Persistence.Database;
using MusicPlayerSyncInterface.DTOs;
using MusicPlayerSyncInterface.DTOs.Composites;

namespace MusicPlayerAvaloniaPort.Services.Song;

[RegisterImplementation(ServiceRegisterType.Singleton, typeof(UpvotedSongSyncService))]
public class UpvotedSongSyncService
{
    HttpClient httpClient;
    IEzAuth authBackend;
    IEzAuthHttpClient? client = null;
    EzAuthAddress? authBackendAddress = null;
    public string State { get => state; private set { OnStateChanged?.Invoke(value); state = value; } }
    private string state = "";
    public Action<string>? OnStateChanged = null;
    JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    const string ROUTE_VERSION_PREFIX = "/v1";

    public UpvotedSongSyncService(HttpClient httpClient, IEzAuth authBackend)
    {
        this.httpClient = httpClient;
        this.authBackend = authBackend;

        Init();
    }

    public void Init(string? password = null, bool TryCallApiInit = false)
    {
        var endpoint = $"{ROUTE_VERSION_PREFIX}/sync/init";
        try
        {
            authBackendAddress = GetAuthBackendAddress(Config.Data.SyncServerHost
                ?? throw new Exception($"{nameof(Config.Data.SyncServerHost)} is null!"))
                ?? throw new Exception($"{nameof(GetAuthBackendAddress)} returned null!");
            client = new KeyCloakHttpClient(authBackendAddress, authBackendRefreshToken =>
            {
                Config.Data.AuthBackendRefreshToken = authBackendRefreshToken;
                Config.Save();
            }, Config.Data.AuthBackendRefreshToken, httpClient);

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
                var sendObjString = JsonSerializer.Serialize(new SyncInitRequest([], [.. songDbContext.UpvotedSongs], [.. songDbContext.SongHistoryEntries]), jsonOptions);
                var sendContent = new StringContent(sendObjString, Encoding.UTF8, "application/json");
                var res = client.PostAsync($"{Config.Data.SyncServerHost}{endpoint}", sendContent).Result;
                State = $"Init {res.StatusCode} {res.Content.ReadAsStringAsync().Result}";
            }
        }
        catch (Exception ex)
        {
            State = $"API Init failed: {ex.Message}";
            return;
        }
    }

    public EzAuthAddress? GetAuthBackendAddress(string? syncServerHost)
    {
        if (syncServerHost == null) return null;
        var res = httpClient.GetAsync($"{syncServerHost}{ROUTE_VERSION_PREFIX}/authBackend").Result;
        var content = res.Content.ReadAsStringAsync().Result;
        var address = JsonSerializer.Deserialize<EzAuthAddress>(content, jsonOptions);
        return address;
    }

    public string GetAccountRegistrationAddress(string? syncServerHost = null) =>
        authBackend.GetAccountRegistrationAddress(
            GetAuthBackendAddress(syncServerHost)?.RealmUrl
            ?? throw new Exception("Got null from GetAuthBackendAddress!"));

    public void Pull()
    {
        var endpoint = $"{ROUTE_VERSION_PREFIX}/sync/pull";
        try
        {
            var res = client!.GetStringAsync($"{Config.Data.SyncServerHost}{endpoint}").Result;
            var pulledData = JsonSerializer.Deserialize<SyncPullResponse>(res, jsonOptions);

            if (pulledData == null)
                throw new Exception("Pulled data was null!");
            if (pulledData.Songs.Count() == 0 || pulledData.HistoryEntries.Count() == 0)
                throw new Exception("Pulled data was empty!");

            Console.WriteLine($"Pulled {pulledData.Songs.Count()} songs and {pulledData.HistoryEntries.Count()} history entries, writing into local db...");

            using var songDbContext = new SongDbContext();
            songDbContext.SongHistoryEntries.RemoveRange(songDbContext.SongHistoryEntries);
            songDbContext.SaveChanges();
            songDbContext.UpvotedSongs.RemoveRange(songDbContext.UpvotedSongs);
            songDbContext.SaveChanges();

            // Add missing user (should just be one, ourselves)
            User pulledUser = pulledData.Users.FirstOrDefault() ?? throw new Exception($"pulledData contains no users!");
            if (!songDbContext.Users.Where(x => x.UserId == pulledUser.UserId).Any())
                songDbContext.Users.Add(pulledUser);
            songDbContext.UpvotedSongs.AddRange(pulledData.Songs);
            songDbContext.SaveChanges();
            songDbContext.SongHistoryEntries.AddRange(pulledData.HistoryEntries);
            songDbContext.SaveChanges();

            State = $"Pull Succeeded!";
        }
        catch (Exception ex)
        {
            State = $"Pull failed: {ex.Message}";
        }
    }

    void SaveUnsyncedData(string newEntryjson, string endpoint, string? error = null, Guid? SongId = null)
    {
        using var songDbContext = new SongDbContext();
        songDbContext.NotYetSyncedData.Add(new NotYetSyncedData(Guid.NewGuid(), endpoint, newEntryjson, error, SongId));
        songDbContext.SaveChanges();
    }

    public void UploadNewSong(UpvotedSong newSong)
    {
        var endpoint = $"{ROUTE_VERSION_PREFIX}/sync/new-song";
        var newSongjson = JsonSerializer.Serialize(newSong, jsonOptions);
        try
        {
            var newSongContent = new StringContent(newSongjson, Encoding.UTF8, "application/json");
            var res = client!.PostAsync($"{Config.Data.SyncServerHost}{endpoint}", newSongContent).Result;

            if (!res.IsSuccessStatusCode && res.StatusCode != System.Net.HttpStatusCode.Conflict)
                SaveUnsyncedData(newSongjson, endpoint, $"{res.IsSuccessStatusCode} {res.Content.ReadAsStringAsync().Result}");

            State = $"UploadNewSong {res.StatusCode} {res.Content.ReadAsStringAsync().Result}";
        }
        catch (Exception ex)
        {
            State = $"UploadNewSong failed: {ex.Message}";

            SaveUnsyncedData(newSongjson, endpoint, ex.Message);
        }
    }

    public void Vote(SongHistoryEntry newEntry)
    {
        var endpoint = $"{ROUTE_VERSION_PREFIX}/sync/vote";
        var newEntryjson = JsonSerializer.Serialize(newEntry, jsonOptions);
        try
        {
            var newEntryContent = new StringContent(newEntryjson, Encoding.UTF8, "application/json");
            var res = client!.PostAsync($"{Config.Data.SyncServerHost}{endpoint}", newEntryContent).Result;

            if (!res.IsSuccessStatusCode && res.StatusCode != System.Net.HttpStatusCode.Conflict)
                SaveUnsyncedData(newEntryjson, endpoint, $"{res.IsSuccessStatusCode} {res.Content.ReadAsStringAsync().Result}", newEntry.SongId);

            State = $"Vote {res.StatusCode} {res.Content.ReadAsStringAsync().Result}";
        }
        catch (Exception ex)
        {
            State = $"Vote failed: {ex.Message}";

            SaveUnsyncedData(newEntryjson, endpoint, ex.Message, newEntry.SongId);
        }
    }
}