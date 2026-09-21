# MusicPlayerAvaloniaPortMobile — Android client

The phone head of the music player. It is **not** a scaled-down desktop window: a phone player is a different
product, so this project has its own single-view UI (library search, artwork, transport, voting, sync sheet)
and its own audio backend — but it shares every piece of *behaviour* with the desktop client through
[`MusicPlayerClientCore`](../MusicPlayerClientCore).

## The player screen

* **Header**: a library search field. Typing lists the best matching songs as a drop down — the same
  modified-Levenshtein ranking the desktop's "play a song quickly" flow uses
  (`SongPlaybackService.FindBestSongMatches`), best first, with the artist/album as a second line when the
  song has tags (and the library-relative folder otherwise, so a single-folder library reads as one line per
  row). Tapping a suggestion plays it and closes the search.
* **No permanent status line.** Routine status ("Library ready — tap play", the startup stages, what the sync
  is doing) goes into the **STATUS** / **SYNC STATE** fields of the sync sheet, where it is looked up rather
  than watched. The area under the search field is reserved for notices: failures and the results of actions
  the UI cannot show by itself, which disappear again after a few seconds. Tapping the vote button produces
  no notice at all — the button changing colour *is* the feedback.
* **Artwork, title/artist, one chip with the vote numbers** (score, likes, dislikes, streak), the seek slider
  and the transport. The title **wraps onto a second line** before it ellipsizes (`TextWrapping=Wrap` with
  `MaxLines=2`): with the default `NoWrap` a long song name was cut off on one line even though two lines
  were allowed.
* **The glyphs are `Path`s on a fixed 24x24 `Canvas` inside a `Viewbox`, not `PathIcon`s.** `PathIcon` places
  a geometry by its own bounds, which put an asymmetric shape visibly off-centre: the play triangle measured
  28 device px (~10 dp) left of its circle's centre. On a fixed canvas the coordinates are exactly where the
  glyph lands, so the icons are centred by construction (verified by measuring the rendered glyph against the
  button's disc: the pause bars now land at 539.5 vs a 540 centre). The two shapes that need it also carry a
  deliberate optical nudge — the play triangle sits slightly right of centre, the heart slightly below it,
  because a triangle reads left-heavy and a heart reads top-heavy.
  **The `Viewbox` is deliberately larger than the glyph** (44 for prev/next, 64 for play/pause, 36 for the
  heart): the geometry only fills part of its 24-unit canvas, and the Viewbox scales the whole canvas — so
  sizing the box to the wanted glyph size makes the icon come out at about half. The rendered sizes are the
  ones measured above (prev/next 61x71 px, heart 71x71 px, play triangle 81 px wide on a phone at ~2.75x).
* **One vote gesture**: a round heart button, icon only (the accent colour is the state, and
  `AutomationProperties.Name` keeps it announceable). It arms the upvote; the shared playback logic casts it —
  see the voting section below.
* **Volume slider, 0..200%.** SoundFlow applies the volume as a gain and only rejects negative values, so
  above 100% really does amplify (that is the point: a quiet recording becomes listenable). The value finally
  handed to the mixer is capped at 2x in `MobileAudioPlayerService.Volume`, because the per-song
  normalization multiplies the user volume and stacking 200% with a large normalization factor would only
  clip.
* **`N songs · M upvoted entries`** at the bottom is the "library is loaded" confirmation.

Things that were removed on purpose, so they do not come back: the five most likely next songs (display-only —
there was nothing to act on), the play-chance and volume-normalization chips (internals of the chooser and the
volume service rather than something a listener uses), the vote button's label and its one-line explanation,
and the always-visible status text.

## What is shared with the desktop client (i.e. behaves identically)

| Behaviour | Shared type (in `MusicPlayerClientCore`) |
|---|---|
| Local `UpvotedSongs` database, listening history, queued sync requests — same EF model and migrations | `Persistence/Database/SongDbContext`, `Database/Model` (interface repo) |
| Server sync: pull (incl. incremental history + song library migrations), votes, song uploads, lazy tag/upload worker | `Services/Infrastructure/SongSyncService` |
| Song registration / matching / duplicate merge | `Services/Infrastructure/DbWrapperService` + `SongFileMatching` (interface repo) |
| Weighted song choosing ("play chance" per song) and the runtime play history | `Services/Song/SongChoosingService`, `Services/Song/SongPlaybackService` |
| Voting: score, streak, likes/dislikes, the "upvote when this song ends" flag, the early-skip downvote | `Services/Song/SongVotingService` (called only from `SongPlaybackService`) |
| Volume normalization (per-song loudness measurement stored on the row, applied as a volume multiplier) | `Services/Song/SongVolumeService` |
| User volume, config, data directory resolution | `Persistence/Configuration/Config`, `Persistence/PersistenceLocations` |

## What is (deliberately) mobile only

* **No FFT / diagram / samples visualization.** The desktop client's spectrum analysis and full-song sample
  array are its most expensive features and no phone music player shows them; on mobile the CPU budget
  belongs to playback and battery life.
* **A lean `MobileAudioPlayerService`** instead of `AudioLibWrapperService`: it plays through the same
  SoundFlow/MiniAudio stack, but measures loudness by *streaming* the file in small chunks (constant memory)
  instead of materializing the whole decoded song.
* **Android plumbing**: sandboxed data directory, `READ_MEDIA_AUDIO` permission, public music folder discovery,
  "open registration page" intents.

`IAudioPlaybackService` is the seam between the two: the shared song services only know that interface, and
each head binds it to its own player in its `ServiceContainerSetup`.

## Building

The project targets `net10.0-android`. It needs

* the **.NET Android workload** — and, importantly, for the *same* SDK feature band the workload is installed
  for. On this machine the workload is installed for SDK **10.0.112** while the default `dotnet` resolution
  picks 10.0.400 (which has no Android workload), so either install the workload for your default SDK
  (`dotnet workload install android`) or build with the matching SDK:

  ```powershell
  $env:JAVA_HOME   = 'C:\Program Files\Android\openjdk\jdk-21.0.8'   # JDK 17+ is required
  $env:ANDROID_HOME = 'C:\Program Files (x86)\Android\android-sdk'
  dotnet "C:\Program Files\dotnet\sdk\10.0.112\MSBuild.dll" `
      MusicPlayerAvaloniaPortMobile.csproj -restore -p:UsedAvaloniaProducts=
  ```

* an **Android SDK** with the platform matching `AndroidCompileSdkVersion` (35) and build tools.

`-p:UsedAvaloniaProducts=` disables Avalonia's build telemetry task, which otherwise tries to write into
`%LOCALAPPDATA%\AvaloniaUI` and fails in restricted environments (the same flag the desktop build uses).

The build produces `bin/<Configuration>/net10.0-android/com.jnccd.musicplayermobile-Signed.apk`.

### Release trims and AOTs (Debug does neither)

```xml
<PropertyGroup Condition="'$(Configuration)' == 'Release'">
  <PublishTrimmed>true</PublishTrimmed>
  <TrimMode>partial</TrimMode>
  <RunAOTCompilation>true</RunAOTCompilation>
  <AndroidEnableProfiledAot>true</AndroidEnableProfiledAot>
</PropertyGroup>
```

Measured APK sizes (arm64 + x86_64):

| Configuration | APK |
|---|---|
| untrimmed, no AOT | 90.2 MB |
| trimmed, no AOT | 52.2 MB |
| **trimmed + profiled AOT (shipped)** | **62.4 MB** |
| trimmed + full AOT | 106.9 MB |

Full AOT is a size *regression*: compiling every method produces more native code than the IL it replaces.
Profile-guided AOT (`AndroidEnableProfiledAot` + the SDK's `dotnet.aotprofile`) compiles the startup paths
that actually matter and stays well below the untrimmed baseline. That is the default the Android SDK would
pick anyway; it is spelled out here so nobody "helpfully" turns it off.

`TrimMode` is pinned to `partial`, and that is the load bearing detail of the whole setup. Partial trimming
only trims assemblies that opt in with `IsTrimmable`, and the linked output proves what that means here
(Android `Release`, arm64):

| Assembly | untrimmed | after linking | trimmed? |
|---|---|---|---|
| `SoundFlow.dll` | 1 032 192 | 143 360 | **yes** (declares `IsAotCompatible`) |
| `Microsoft.Extensions.DependencyInjection.dll` | 96 056 | 46 080 | **yes** |
| `MusicPlayerClientCore.dll` | 125 440 | 125 440 | no |
| `MusicPlayerAvaloniaPortMobile.dll` | 105 472 | 105 472 | no |
| `MusicPlayerSyncInterface.dll` | 27 136 | 27 136 | no |
| `Microsoft.EntityFrameworkCore.dll` | 2 533 408 | 2 533 408 | no |
| `taglib-sharp.dll` | 491 008 | 491 008 | no |

So the size win comes from the runtime/UI side, while everything that works by reflection - the service
container's `Assembly.GetTypes()` scan, EF Core's model building, TagLib's file type resolution - keeps every
type and constructor it needs. `TrimMode=full` would trim those too and break all three (see below).

### Escape hatches

If the Release build misbehaves on a device, in order of least lost:

1. Keep trimming, exempt one assembly: `<TrimmerRootAssembly Include="SoundFlow" />` (SoundFlow is the only
   library we depend on that is actually trimmed - it shrinks by 86%, so it is the first suspect).
2. Turn both off: `-p:PublishTrimmed=false -p:RunAOTCompilation=false`. They must go together, because the
   Android SDK rejects AOT without trimming (`XA1030`) - and note that
   `Microsoft.Android.Sdk.DefaultProperties.targets` enables AOT by default for `Release`, so setting
   `PublishTrimmed=false` alone makes `Release` fail while `Debug` still succeeds.

### Native libraries

SoundFlow ships `libminiaudio.so` under `runtimes/android-*/native`. A plain project reference copies those
files into the output folder, which is **not** where Android loads native libraries from, so the project
re-declares them as `AndroidNativeLibrary` items with an ABI. Verified in the built APK:

```
lib/arm64-v8a/libminiaudio.so   lib/x86_64/libminiaudio.so
lib/arm64-v8a/libe_sqlite3.so   lib/x86_64/libe_sqlite3.so
lib/arm64-v8a/libSkiaSharp.so   lib/x86_64/libSkiaSharp.so
```

## Using it

1. Put mp3 files on the device (the standard `Music` folder of shared storage is found automatically).
2. Grant the media read permission when asked.
3. The app scans the folder, builds the choosing pool and starts. `Sync` in the header opens the settings
   sheet: server host, account, password, log in & sync, rescan, and the "take the library over" prompt when
   the library belongs to another account.

## Diagnosing on a device

Everything the client logs goes to logcat under one tag, in **Release builds too**:

```powershell
adb logcat -s MusicPlayerMobile        # live
adb logcat -d -s MusicPlayerMobile     # dump what is already buffered
adb logcat -d -b crash                 # the crash buffer
```

`MobileLog` exists because the first phone crash was effectively invisible: Android's crash dialog only
offers "send the summary to the OS developers", `Debug.WriteLine` is compiled out of Release, and the
`Console.WriteLine` calls the shared core is full of never reach logcat in a Release build. Startup stages,
why a folder was accepted or rejected, audio device/format, loudness measurements and every unhandled
exception (via `AndroidEnvironment.UnhandledExceptionRaiser`, `AppDomain.UnhandledException` and
`TaskScheduler.UnobservedTaskException`) end up under that tag.

The settings sheet additionally shows **where music was looked for and why each place was rejected**, and the
folder can be typed in by hand — the automatic detection only knows Android's public `Music` directory, and
music on a phone is often somewhere else.

## Sync session: log in, log in without uploading, log out

The sync sheet has three session controls plus the current state ("signed in as X" / "not signed in"):

| Control | What it does |
|---|---|
| **Log in & upload** | `Init(password, TryCallApiInit: true)`: authenticates, then seeds the server account from the local library with `POST /sync/init`, retries queued votes/uploads (`RetryUnsyncedEntries: true`), and pulls. |
| **Log in (no upload)** | The same, but with `TryCallApiInit: false` **and** `RetryUnsyncedEntries: false`: it pulls only. Nothing is sent to the server — not the library, not queued votes. |
| **Log out** | Forgets the session (`SongSyncService.Logout()`): drops the in-memory client and clears the stored refresh token. |

Notes:

* **Logging out uploads nothing** and does not touch the local database or the configured account name — the
  library keeps working offline, and logging back in as the same account continues where it left off.
* `/sync/init` is the *whole-library* upload, and the server answers `409` when the account already has songs
  or history — exactly the case "log in (no upload)" exists for. It is also safe to use by accident: a full
  pull against an empty account is rejected by the client before it rewrites anything, so the local library
  survives and the ordinary log in can seed the account afterwards.
* Logging out only forgets the token **locally**; there is no Keycloak logout call, because `EzAuth` (a
  submodule shared with the other clients) exposes no logout endpoint. The refresh token therefore stays valid
  server-side until it expires, which is what makes logging back in without a password work.
* A logged-out client makes **no** requests at all: `Pull()` and the upload paths check the session and report
  "Not logged in …" instead of dereferencing a null client. `UploadNewSongEntry` queues the song rather than
  losing it, and `UploadQueuedVote` keeps its marker, so nothing is lost while logged out.

Both behaviours are covered by a harness (`.build-check/sync-login-harness`, not part of any repo) that points
the real `SongSyncService` at a fake sync server and asserts which endpoints get called:

```
log in without upload: GET /v1/authBackend, POST token, GET /v1/sync/pull      ← no /v1/sync/init
ordinary login:        GET /v1/authBackend, POST token, POST /v1/sync/init     ← the flag gates the upload
after logout:          <none>   (token cleared, database untouched)
SYNC LOGIN HARNESS RESULT: PASS
```

## Media notification & background playback

`MobilePlaybackNotificationService` is the ongoing media notification every Android player has: cover art,
title, artist/album and previous / play-pause / next, on the lock screen and in the shade.

* **It is a foreground service** (type `mediaPlayback`), not just a notification. That is what Android
  requires for a persistent media notification, and it is also what keeps the process — and playback — alive
  when the user leaves the app. Background playback therefore works.
* **It drives the same shared services the UI drives.** The buttons call
  `SongPlaybackService.GetNextSong`/`GetPreviousSong` and `MobileAudioPlayerService.TogglePlayPause`, so
  skipping from the notification runs the identical vote logic as skipping in the app (there is no second
  player state to drift).
* **Platform APIs only** — `Notification.MediaStyle` + `MediaSession`, no AndroidX Media dependency, so
  nothing new enters the trimming/AOT analysis.
* `POST_NOTIFICATIONS` is requested at runtime on API 33+ (denying it does not break playback; the service
  runs and simply shows nothing). `FOREGROUND_SERVICE` and `FOREGROUND_SERVICE_MEDIA_PLAYBACK` are declared.
* The `MediaSession` also serves the lock screen, bluetooth and headset transport buttons, and carries the
  `MediaMetadata`.

Two things worth knowing if you touch it:

* The **progress row reads the duration from `MediaMetadata`, not from the playback state.** Publishing only
  `PlaybackState` gives a correct position but still renders `00:00 / 00:00`, so the service publishes
  metadata too — and republishes when the duration appears, because right after a song starts the decoder has
  not reported it yet (a once-per-second session refresh covers that; the bar itself is extrapolated by the
  system from the last published state).
* Starting a foreground service from the background is forbidden on Android 12+, so the service is started
  when a song starts playing — which always happens from the UI, a legal moment — and only *updated* on later
  song changes (auto-advance included).

## Accent colour: defined once, and it overrides the platform's

`MobileApp.axaml` holds the app's accent (`AppAccentBrush` `#00BEC8` plus its hover shade and the ink colour
for text on it) and also overrides Fluent's `SystemAccentColor`.

That override is not cosmetic housekeeping — without it the two halves of the UI disagree. The theme colours
accent-driven controls itself: the slider's filled track and thumb, the progress bar, focus visuals. Left
alone, `SystemAccentColor` comes from the **platform**, so on Android the sliders picked up the device's
theme colour — MIUI handed us an olive `#717C35` — while this app's own brushes hardcoded teal. The seek and
volume sliders therefore did not match the play/pause button.

Measured on the device, counting pixels of each colour in the same screen:

| | platform olive `#717C35` | app teal `#00BEC8` |
|---|---|---|
| before | 2178 px | 9870 px |
| after | 30 px (one album-art pixel) | 12287 px |

Changing the accent is now a one-line edit in `MobileApp.axaml`.

## Voting: one gesture, no voting code in the UI

The player has a single `Upvote` control — a round, icon-only heart. It does not call `SongVotingService`: it
flips `SongPlaybackService.UpvoteLockedIn`, and the *shared* playback logic casts the vote — when the song
ends or is skipped (`GetNextSong`/`GetPreviousSong`). A song skipped early is voted down by that same logic,
so the downvote needs no button either: play on and press next, or skip early and it counts against the song.
This is exactly the desktop client's behaviour, and it keeps the vote rules in one place instead of
duplicating them in the UI (an earlier revision had `Dislike` / `Lock in` / `Upvote` buttons that called the
voting service directly).

The chip row still shows `score`, `▲`/`▼` and `streak` for the current song, refreshed from the row whenever
a vote or a loudness measurement lands.

## Launcher icon

The icon is the desktop client's `MusicPlayerAvaloniaPort/Assets/icon.ico`, converted — Android cannot load
`.ico`, so the **256×256 PNG frame embedded in that file is extracted losslessly** (ICO directory at offset 6,
16 bytes per entry, `bytesInRes` at +8, image offset at +12; the frame whose data starts with the PNG
signature) and downscaled with `InterpolationMode.HighQualityBicubic`.

Two forms are shipped, because they do different jobs:

| Resource | Used by | Notes |
|---|---|---|
| `mipmap-*/ic_launcher.png` | Android < 8 | Full-bleed bitmap at 48/72/96/144/192 px |
| `mipmap-anydpi-v26/ic_launcher.xml` (+ `ic_launcher_round.xml`) | Android 8+ | Adaptive icon: `@color/ic_launcher_background` + `mipmap-*/ic_launcher_foreground.png` |
| `mipmap-*/ic_launcher_foreground.png` | via the adaptive icon | 108 dp canvas (108/162/216/324/432 px) with the mark inside the central 66 dp safe zone, so no launcher mask clips it |

**The adaptive icon background must be a real colour.** A launcher always draws a shape behind an app icon and
fills it with whatever the icon declares as its background, so declaring
`@android:color/transparent` does *not* produce a background-less icon:

* MIUI (and its global launcher) painted it **black** — the app icon looked like it had a black background;
* with only a legacy bitmap (no adaptive icon at all) the same artwork got MIUI's **white** plate instead;
* a launcher that does honour transparency would let the wallpaper through, so the icon would look different
  per device.

The artwork itself is genuinely transparent either way — the extracted PNG's corners are `A=0` and only ~313 of
65536 pixels are partially transparent (the anti-aliased edge), which was worth verifying before blaming the
launcher. Declaring `ic_launcher_background` (the app's own `#0B0B12`) makes the icon look the same
everywhere and consistent with the UI; changing that one colour in `values/colors.xml` re-themes it.

## Verified on hardware

The Release APK has been installed and exercised on a real Android phone (arm64): the library scan finds the
songs, playback starts, cover art is read out of the mp3, volume normalization measures and stores the
loudness, the upvote gesture arms and casts, the media notification appears with art/controls/progress and
its buttons drive playback (verified through logcat), and the launcher icon resolves at all five densities —
with an empty crash buffer throughout. `adb`-driven checks are the way to repeat this:

```powershell
adb install -r bin/Release/net10.0-android/com.jnccd.musicplayermobile-Signed.apk
adb shell am force-stop com.jnccd.musicplayermobile
adb shell monkey -p com.jnccd.musicplayermobile -c android.intent.category.LAUNCHER 1
```

### Why the first Release build crashed at startup

For the record, because it was not a trimming problem at all - the trim/AOT configuration above is fine:

```
System.ArgumentNullException: Arg_ParamName_Name, source
  at System.Linq.Enumerable.First
  at MusicPlayerAvaloniaPortMobile.Services.MobileAudioPlayerService..ctor
```

The player used to look up its output device with `PlaybackDevices.FirstOrDefault(d => d.IsDefault)` and then
index that device's `SupportedDataFormats`. Android enumerates exactly one device and does **not** flag it as
default, so `FirstOrDefault` returned `default(DeviceInfo)` - a struct whose `SupportedDataFormats` is null -
and `.First()` on it threw inside the constructor. Because the constructor runs during dependency injection,
the exception took the whole app down before the UI existed (which is why the stack trace looks like a DI
failure).

Both halves of the fix matter:

* the device is now optional - `InitializePlaybackDevice` accepts a nullable `DeviceInfo`, and null simply
  means "let miniaudio open the system default", which is what a phone wants; the format falls back to
  48 kHz stereo F32 when the device reports none;
* audio setup can no longer be fatal - the engine and output device are created on first use inside a
  `try`/`catch`, and a failure is reported through `IsAvailable`/`LastError`, which the status line shows.

## Known limitations

* **`warning XA0141` about 16 KB page sizes** comes from the third party
  `SQLitePCLRaw.lib.e_sqlite3.android 2.1.6` package (pulled in by EF Core 8) — its `libe_sqlite3.so` is not
  built for Android 16's 16 KB pages. It is a warning, not a failure; fixing it means upgrading the
  SQLitePCLRaw/EF Core stack, which the shared interface submodule pins.
* **Playback is verified on one device / one Android version.** The audio path is inherently the least
  portable part; if another device has trouble, `adb logcat -s MusicPlayerMobile` says whether the output
  device opened and with which format before anything else needs guessing.
* **Playback continues in the background** through the media notification's foreground service (see above),
  but there is no `MediaButtonReceiver`, so a headset button press is handled while the session is active and
  not after the process was killed and restarted by the system.
* **Song library migrations (file renames/deletes) may fail on modern Android.** Since Android 10 the app
  may read media files by path but cannot rename/delete files in shared storage without broad storage
  access. The shared applier is existence-tolerant and does not advance its state on failure, so this is
  logged and retried, never a crash — but a rename performed on the desktop will not be mirrored on the
  phone's local copy.
* The sync sheet is the only settings UI — there is no statistics/history screen yet, as requested (main view
  only).
