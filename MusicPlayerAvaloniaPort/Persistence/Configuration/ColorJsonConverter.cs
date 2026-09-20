using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media;

namespace MusicPlayerAvaloniaPort.Persistence.Configuration;

/// <summary>
/// Stores an Avalonia <see cref="Color"/> as a single "#RRGGBB" (or "#AARRGGBB") string.
///
/// Without this converter System.Text.Json silently loses the value: <see cref="Color"/> only exposes
/// get-only A/R/G/B properties, so nothing can be written back into the struct and every load yields
/// transparent black - which is exactly what the "PrimaryColor": { "A": 0, ... } objects in existing
/// config files are. That legacy object form is still read, so old configs keep loading.
/// </summary>
public class ColorJsonConverter : JsonConverter<Color>
{
    public override Color Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return Color.TryParse(reader.GetString(), out Color parsed) ? parsed : default;
            case JsonTokenType.StartObject:
                return ReadLegacyObject(ref reader);
            default:
                return default;
        }
    }

    public override void Write(Utf8JsonWriter writer, Color value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.A == 255
            ? $"#{value.R:X2}{value.G:X2}{value.B:X2}"
            : $"#{value.A:X2}{value.R:X2}{value.G:X2}{value.B:X2}");
    }

    /// <summary>Reads the {"A":..,"R":..,"G":..,"B":..} form that older versions wrote.</summary>
    static Color ReadLegacyObject(ref Utf8JsonReader reader)
    {
        byte a = 255, r = 0, g = 0, b = 0;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            string channelName = reader.GetString() ?? "";
            if (!reader.Read() || !reader.TryGetByte(out byte channelValue))
                continue;

            switch (channelName.ToUpperInvariant())
            {
                case "A": a = channelValue; break;
                case "R": r = channelValue; break;
                case "G": g = channelValue; break;
                case "B": b = channelValue; break;
            }
        }

        return Color.FromArgb(a, r, g, b);
    }
}
