{
  # Nix package for music-player-avalonia-port.
  #
  # Only the desktop app is packaged: SoundFlow, Tmds.DBus, EzAuth and
  # MusicPlayerSyncInterface are build inputs, not outputs.
  #
  # The submodules are part of the source, and nix's git fetcher does NOT include
  # them by default, so builds need the submodules flag:
  #
  #   nix build 'git+file:///path/to/music-player-avalonia-port?submodules=1'
  #
  # (`nix build .#default` from inside the checkout sees empty submodule
  # directories and fails.)
  pkgs,
  lib,
}:

let
  version = "0.1.0";

  # Avalonia renders through Skia. The project pulls in
  # SkiaSharp.NativeAssets.Linux.NoDependencies, so Skia arrives without the
  # fontconfig/freetype it would otherwise need, but the X11/GL stack still has
  # to be on LD_LIBRARY_PATH or startup dies with
  # "Unable to load shared library 'libSkiaSharp'".
  avaloniaNativeLibs = with pkgs; [
    fontconfig
    freetype
    libGL
    harfbuzz
    icu
    zlib

    libx11
    libice
    libsm
    libxcb
    libxrandr
    libxi
    libxcursor
    libxext
    libxrender
    libxkbcommon
  ];

  # Everything but build output, so a local build tree cannot leak into the
  # derivation.
  src = lib.cleanSourceWith {
    src = ../.;
    filter =
      path: _: !(builtins.elem (baseNameOf path) [
        "bin"
        "obj"
      ]);
  };
in
{
  # `nix build` / `nix build .#` -> the desktop app.
  desktop = pkgs.buildDotnetModule {
    pname = "music-player";
    inherit version src;

    projectFile = "MusicPlayerAvaloniaPort/MusicPlayerAvaloniaPort.csproj";
    dotnet-sdk = pkgs.dotnetCorePackages.sdk_10_0;
    dotnet-runtime = pkgs.dotnetCorePackages.runtime_10_0;

    selfContainedBuild = true;
    runtimeId = "linux-x64";

    # Regenerate with a RID-aware restore after changing package references,
    # otherwise the net8.0 projects in this graph (EzAuth,
    # MusicPlayerSyncInterface) are missing their host pack:
    #   dotnet restore MusicPlayerAvaloniaPort/MusicPlayerAvaloniaPort.csproj -r linux-x64
    nugetDeps = ../nuget-deps.json;

    # buildDotnetModule installs the published app under $out/lib/<pname>; this
    # puts the executable in $out/bin and wraps it with runtimeDeps'
    # LD_LIBRARY_PATH.
    executables = [ "MusicPlayerAvaloniaPort" ];
    runtimeDeps = avaloniaNativeLibs ++ [ pkgs.pulseaudio ];

    # YoutubeHelper looks for a bundled yt-dlp next to the app and otherwise
    # falls back to "yt-dlp" on PATH, so put the nixpkgs one on PATH. It is a
    # program, not a library, which is why it cannot go in runtimeDeps.
    #
    # The `cd` is for an app bug: AvaloniaWindowManager loads its window icon via
    # the relative path "Assets/icon.ico", so it only resolves when the process
    # runs from the directory holding Assets/. start_desktop_app.sh happens to cd
    # into the project directory first, which is why the client-side build works
    # and a bare binary does not - without this the app dies instantly with
    # DirectoryNotFoundException and only writes ./error.log. The real fix
    # belongs in the app (AppContext.BaseDirectory instead of a relative path).
    postFixup = ''
      wrapProgram $out/bin/MusicPlayerAvaloniaPort \
        --prefix PATH : ${lib.makeBinPath [ pkgs.yt-dlp ]} \
        --run "cd $out/lib/music-player"
    '';

    meta = {
      description = "Music player desktop app (Avalonia)";
      license = lib.licenses.mit;
      platforms = lib.platforms.linux;
      mainProgram = "MusicPlayerAvaloniaPort";
    };
  };
}
