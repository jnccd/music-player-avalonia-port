#!/usr/bin/env bash
cd ./MusicPlayerAvaloniaPort
DB_PROVIDER=sqlite MUSIC_PLAYER_SQLITE_DB_PATH=./bin/Release/net10.0/ dotnet ef database update
dotnet run -c Release
