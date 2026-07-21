#!/usr/bin/env bash
cd ./MusicPlayerAvaloniaPort

if [ -z "$NIXOS_JNCCD_GUI_STARTER_UNCHANGED" ] || [ "$NIXOS_JNCCD_GUI_STARTER_UNCHANGED" != "1" ]; then
  # Changed - run database update and rebuild
  DB_PROVIDER=sqlite MUSIC_PLAYER_SQLITE_DB_PATH=./bin/Release/net10.0/ dotnet ef database update
  dotnet run -c Release
else
  # Unchanged - just run the existing DLL with DB vars
  DB_PROVIDER=sqlite MUSIC_PLAYER_SQLITE_DB_PATH=./bin/Release/net10.0/ dotnet ./bin/Release/net10.0/MusicPlayerAvaloniaPort.dll
fi
