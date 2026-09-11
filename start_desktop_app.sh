#!/usr/bin/env bash
cd "$(dirname "$0")/MusicPlayerAvaloniaPort"

# The SQLite database and the config live in the user's data directory - on Linux
# $XDG_DATA_HOME/MusicPlayerAvaloniaPort-Release (~/.local/share/... by default), see
# MusicPlayerAvaloniaPort/Persistence/PersistenceLocations.cs - and the app creates that database and
# applies its own EF Core migrations on startup (DbWrapperService.EnsureDatabaseUpToDate). So this script
# no longer has to set any database environment variables or run "dotnet ef database update", and it does
# not matter where it is run from or where the build output goes.

if [ -z "$NIXOS_JNCCD_GUI_STARTER_UNCHANGED" ] || [ "$NIXOS_JNCCD_GUI_STARTER_UNCHANGED" != "1" ]; then
  # Changed - rebuild and run
  dotnet run -c Release
else
  # Unchanged - just run the existing DLL
  dotnet ./bin/Release/net10.0/MusicPlayerAvaloniaPort.dll
fi
