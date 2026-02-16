using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;

namespace MusicPlayerAvaloniaPort;

public class Helpers
{
    public static List<string> FindAllMp3FilesInDir(string StartDir)
    {
        List<string> re = new();
        foreach (string s in Directory.GetFiles(StartDir))
            if (s.EndsWith(".mp3"))
            {
                re.Add(s);
            }

        foreach (string D in Directory.GetDirectories(StartDir))
            re.AddRange(FindAllMp3FilesInDir(D));

        return re;
    }
    public static bool DirOrSubDirsContainMp3(string StartDir)
    {
        foreach (string s in Directory.GetFiles(StartDir))
            if (s.EndsWith(".mp3"))
                return true;

        foreach (string D in Directory.GetDirectories(StartDir))
            if (DirOrSubDirsContainMp3(D))
                return true;
        return false;
    }

    public static Stream ModifyImagePixels(SKBitmap bitmap, Color col)
    {
        // Lock the pixels
        IntPtr pixels = bitmap.GetPixels(out var pixelInfo);
        int width = bitmap.Width;
        int height = bitmap.Height;

        unsafe
        {
            // Cast the pixel buffer to a byte pointer
            byte* pixelPtr = (byte*)pixels.ToPointer();

            // Iterate over each pixel row
            for (int y = 0; y < height; y++)
            {
                // Get the start of the row
                byte* rowPtr = pixelPtr + (y * bitmap.RowBytes);

                // Iterate over each pixel in the row
                for (int x = 0; x < width; x++)
                {
                    // Calculate the offset for the current pixel (assuming 32-bit RGBA)
                    byte* pixel = rowPtr + (x * 4);

                    // Access and modify the pixel channels (RGBA order)
                    // byte r = pixel[2];
                    // byte g = pixel[1];
                    // byte b = pixel[0];
                    byte a = pixel[3];

                    // Example: Apply a red tint
                    pixel[2] = col.R;
                    pixel[1] = col.G;
                    pixel[0] = col.B;
                    pixel[3] = a;
                }
            }
        }

        // Save the modified image
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.AsStream();
    }
}