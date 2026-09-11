using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace MusicPlayerAvaloniaPort.Persistence.Configuration;

public static class Config
{
    static readonly object lockject = new object();
    // Both files live in the app's data directory now (on Linux below $XDG_DATA_HOME instead of next to
    // the executable, see PersistenceLocations).
    static readonly string configPath = PersistenceLocations.ConfigPath;
    static readonly string configBackupPath = PersistenceLocations.ConfigBackupPath;
    public static bool UnsavedChanges = false;
    public static ConfigData Data
    {
        get
        {
            lock (lockject)
            {
                UnsavedChanges = true;
                return data;
            }
        }
        set
        {
            UnsavedChanges = true;
            data = value;
        }
    }
    private static ConfigData data = new ConfigData();
    static JsonSerializerOptions jsonOptionsSerialize = new()
    {
        WriteIndented = true
    };
    static JsonSerializerOptions jsonOptionsDeserialize = new()
    {
        PropertyNameCaseInsensitive = true
    };

    static Config()
    {
        if (Config.Exists())
            Config.Load();
        else
            Config.Data = new ConfigData();
    }

    public static string GetConfigPath()
    {
        return configPath;
    }
    public static bool Exists()
    {
        return File.Exists(configPath);
    }
    public static void Save()
    {
        lock (lockject)
        {
            // The data directory is user territory now and may have been cleaned up while the app runs.
            Directory.CreateDirectory(PersistenceLocations.DataDirectory);

            if (File.Exists(configPath))
                File.Copy(configPath, configBackupPath, true);
            var cereal = JsonSerializer.Serialize(Data, jsonOptionsSerialize);
            File.WriteAllText(configPath, cereal);

            UnsavedChanges = false;
        }
    }
    public static void Load()
    {
        lock (lockject)
        {
            if (Exists())
                Data = JsonSerializer.Deserialize<ConfigData>(File.ReadAllText(configPath), jsonOptionsDeserialize) ?? Data;
            else
                Data = new ConfigData();
        }
    }
    public static void LoadFrom(string JSON)
    {
        lock (lockject)
        {
            Data = JsonSerializer.Deserialize<ConfigData>(JSON, jsonOptionsDeserialize) ?? Data;
        }
    }
    public static new string ToString()
    {
        string output = "";

        FieldInfo[] Infos = typeof(ConfigData).GetFields();
        foreach (FieldInfo info in Infos)
        {
            output += "\n" + info.Name + ": ";

            if (info.FieldType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(info.FieldType))
            {
                output += "\n";
                IEnumerable a = info.GetValue(Data) as IEnumerable ?? Array.Empty<string>();
                IEnumerator e = a.GetEnumerator();
                e.Reset();
                while (e.MoveNext())
                {
                    output += e.Current + ", ";
                }
            }
            else
            {
                output += info.GetValue(Data) + "\n";
            }
        }

        return output;
    }
}
