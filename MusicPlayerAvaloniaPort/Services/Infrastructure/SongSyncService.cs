using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using EzAuth.Interfaces;
using EzAuth.Keycloak;
using MusicPlayerAvaloniaPort.Persistence.Configuration;
using MusicPlayerSyncInterface.DTOs;
using MusicPlayerSyncInterface.DTOs.Composites;

namespace MusicPlayerAvaloniaPort.Services.Infrastructure;

[RegisterImplementation(ServiceRegisterType.Singleton, typeof(SongSyncService))]
public class SongSyncService
{
    readonly HttpClient HttpClient;
    readonly IEzAuth AuthBackend;
    readonly DbWrapperService DbWrapper;

    private IEzAuthHttpClient? client = null;
    EzAuthAddress? authBackendAddress = null;
    public string State { get => state; private set { OnStateChanged?.Invoke(value); state = value; } }
    private string state = "";
    public Action<string>? OnStateChanged = null;
    readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    const string ROUTE_VERSION_PREFIX = "/v1";

    public SongSyncService(HttpClient HttpClient, IEzAuth AuthBackend, DbWrapperService DbWrapper)
    {
        this.HttpClient = HttpClient;
        this.AuthBackend = AuthBackend;
        this.DbWrapper = DbWrapper;

        Init();
    }

    public void Init(string? password = null, bool TryCallApiInit = false, bool RetryUnsyncedEntries = true)
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
            }, Config.Data.AuthBackendRefreshToken, HttpClient);

            if (password != null)
                client.Login(Config.Data.SyncServerUsername ?? throw new Exception($"{nameof(Config.Data.SyncServerUsername)} is null!"), password);
        }
        catch (Exception ex)
        {
            State = $"SyncManager Init failed: {ex.Message}";
            return;
        }

        // Init
        try
        {
            if (TryCallApiInit)
            {
                using var dbContext = DbWrapper.GetContext();
                var initRequest = dbContext.GetSyncInitRequest();
                var sendObjString = JsonSerializer.Serialize(initRequest, jsonOptions);
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

        // Retry unsynced entries
        if (RetryUnsyncedEntries)
        {
            using var dbContext = DbWrapper.GetContext();
            var first20UnsyncedReqs = dbContext.GetNotYetSyncedDataEntries().Take(20);
            foreach (var unsyncedData in first20UnsyncedReqs)
            {
                try
                {
                    var sendContent = new StringContent(unsyncedData.Body, Encoding.UTF8, "application/json");
                    HttpResponseMessage res;
                    if (unsyncedData.Endpoint == "/sync/volume") // Horrible way to do this, TODO: change unsyncedData to include HTTP method type (POST/PUT) instead of hardcoding it here
                        res = client.PutAsync($"{Config.Data.SyncServerHost}{ROUTE_VERSION_PREFIX}{unsyncedData.Endpoint}", sendContent).Result;
                    else
                        res = client.PostAsync($"{Config.Data.SyncServerHost}{ROUTE_VERSION_PREFIX}{unsyncedData.Endpoint}", sendContent).Result;

                    Console.WriteLine($"Synced data for endpoint {unsyncedData.Endpoint}: {res.StatusCode}, {unsyncedData.Body}");

                    if (res.IsSuccessStatusCode || res.StatusCode == System.Net.HttpStatusCode.Conflict)
                    {
                        dbContext.RemoveNotYetSyncedDataEntries(unsyncedData);
                    }
                }
                catch (Exception ex)
                {
                    State = $"API Retry Unsynced Entries failed: {ex.Message}";
                    Console.WriteLine($"API Retry Unsynced Entries failed for endpoint {unsyncedData.Endpoint}: {ex.Message}, {unsyncedData.Body}");
                }
            }
        }
    }

    public EzAuthAddress? GetAuthBackendAddress(string? syncServerHost)
    {
        if (syncServerHost == null) return null;
        var res = HttpClient.GetAsync($"{syncServerHost}{ROUTE_VERSION_PREFIX}/authBackend").Result;
        var content = res.Content.ReadAsStringAsync().Result;
        var address = JsonSerializer.Deserialize<EzAuthAddress>(content, jsonOptions);
        return address;
    }

    public string GetAccountRegistrationAddress(string? syncServerHost = null) =>
        AuthBackend.GetAccountRegistrationAddress(
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

            using var dbContext = DbWrapper.GetContext();
            dbContext.RewriteDatabase(pulledData);

            State = $"Pull Succeeded!";
        }
        catch (Exception ex)
        {
            State = $"Pull failed: {ex.Message}";
        }
    }

    public void UploadNewSongEntry(UpvotedSong newSong)
    {
        var endpoint = $"{ROUTE_VERSION_PREFIX}/sync/new-song";
        var newSongJson = JsonSerializer.Serialize(newSong, jsonOptions);
        using var dbContext = DbWrapper.GetContext();
        try
        {
            var newSongContent = new StringContent(newSongJson, Encoding.UTF8, "application/json");
            var res = client!.PostAsync($"{Config.Data.SyncServerHost}{endpoint}", newSongContent).Result;

            if (!res.IsSuccessStatusCode && res.StatusCode != System.Net.HttpStatusCode.Conflict)
                dbContext.AddNewNotYetSyncedDataEntry(newSongJson, endpoint, $"{res.IsSuccessStatusCode} {res.Content.ReadAsStringAsync().Result}", newSong.SongId);

            State = $"UploadNewSong {res.StatusCode} {res.Content.ReadAsStringAsync().Result}";
        }
        catch (Exception ex)
        {
            State = $"UploadNewSong failed: {ex.Message}";

            dbContext.AddNewNotYetSyncedDataEntry(newSongJson, endpoint, ex.Message, newSong.SongId);
        }
    }

    public void Vote(SongHistoryEntry newEntry)
    {
        var endpoint = $"{ROUTE_VERSION_PREFIX}/sync/vote";
        var newEntryJson = JsonSerializer.Serialize(newEntry, jsonOptions);
        using var dbContext = DbWrapper.GetContext();
        try
        {
            var newEntryContent = new StringContent(newEntryJson, Encoding.UTF8, "application/json");
            var res = client!.PostAsync($"{Config.Data.SyncServerHost}{endpoint}", newEntryContent).Result;

            if (!res.IsSuccessStatusCode && res.StatusCode != System.Net.HttpStatusCode.Conflict)
                dbContext.AddNewNotYetSyncedDataEntry(newEntryJson, endpoint, $"{res.IsSuccessStatusCode} {res.Content.ReadAsStringAsync().Result}", newEntry.SongId);

            State = $"Vote {res.StatusCode} {res.Content.ReadAsStringAsync().Result}";
        }
        catch (Exception ex)
        {
            State = $"Vote failed: {ex.Message}";

            dbContext.AddNewNotYetSyncedDataEntry(newEntryJson, endpoint, ex.Message, newEntry.SongId);
        }
    }
}