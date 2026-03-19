using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace MusicPlayerAvaloniaPort.Converters;

public class EitherOrConverter : IValueConverter
{

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            string[] @params = (parameter as string)!.Split("|");
            object obj1, obj2;

            if (targetType == typeof(IImage))
            {
                obj1 = new Bitmap(@params[0]);
                obj2 = new Bitmap(@params[1]);
            }
            else
            {
                obj1 = System.Convert.ChangeType(@params[0], targetType);
                obj2 = System.Convert.ChangeType(@params[1], targetType);
            }

            return (value is bool b) && b ? obj1 : obj2;
        }
        catch (Exception e) { Debug.WriteLine(e); }
        return default;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}