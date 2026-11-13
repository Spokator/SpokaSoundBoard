using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace SpokaSoundBoard.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is true;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Visible ^ Invert;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var visible = value != null && value is not "";
        if (Invert) visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the bound value equals the converter parameter (used for page switching).</summary>
public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when the bound value equals the parameter; used by navigation radio buttons.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? parameter : Binding.DoNothing;
}

public sealed class HexToBrushConverter : IValueConverter
{
    public double Opacity { get; set; } = 1;

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(value as string ?? "#7C5CFF");
            var brush = new SolidColorBrush(color) { Opacity = Opacity };
            brush.Freeze();
            return brush;
        }
        catch
        {
            return Brushes.SlateBlue;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>0..1 float to a "75 %" string.</summary>
public sealed class PercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is float or double ? $"{System.Convert.ToDouble(value) * 100:0} %" : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Formats a dB value with sign, e.g. "+3.0 dB".</summary>
public sealed class DbConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var v = System.Convert.ToDouble(value);
        return v > 0.05 ? $"+{v:0.0} dB" : $"{v:0.0} dB";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>bool? ↔ index of « Ne pas changer / Activer / Désactiver » (game profiles).</summary>
public sealed class NullableBoolToIndexConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        true => 1,
        false => 2,
        _ => 0,
    };

    public object? ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        1 => true,
        2 => false,
        _ => null,
    };
}

/// <summary>string? ↔ combo box value: null (« ne pas changer ») is carried by a token, a combo box cannot select null.</summary>
public sealed class KeepChoiceConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value as string ?? ViewModels.ProfileChoice.Keep;

    public object? ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string s && s != ViewModels.ProfileChoice.Keep ? s : null;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

public sealed class RepeatBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is SpokaSoundBoard.Core.Models.RepeatMode.Off
            ? Application.Current.FindResource("SubtleTextBrush")
            : Application.Current.FindResource("Accent2Brush");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when every bound value is true.</summary>
public sealed class AllTrueToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.All(v => v is true) ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when all bound values are equal (as strings).</summary>
public sealed class MultiEqualsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length >= 2 && values.All(v => string.Equals(v?.ToString(), values[0]?.ToString(), StringComparison.Ordinal));

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Cover / thumbnail (http URL or local file) to a downscaled, cached, frozen image. Returns null (no image)
/// instead of throwing for missing files or bad URLs.
/// </summary>
public sealed class CoverConverter : IValueConverter
{
    private static readonly Dictionary<string, System.Windows.Media.Imaging.BitmapImage> Cache = [];
    private static readonly LinkedList<string> Order = new();
    private const int MaxCached = 400;

    /// <summary>Decode width in pixels (keeps memory low for list thumbnails).</summary>
    public int DecodeWidth { get; set; } = 96;

    public object? Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string source || source.Length == 0) return null;
        var key = DecodeWidth + "|" + source;
        if (Cache.TryGetValue(key, out var cached)) return cached;
        try
        {
            Uri uri;
            if (Uri.TryCreate(source, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)) uri = u;
            else if (System.IO.File.Exists(source)) uri = new Uri(source);
            else return null;

            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.UriSource = uri;
            image.DecodePixelWidth = DecodeWidth;
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile;
            image.EndInit();
            if (!image.IsDownloading) image.Freeze();
            Cache[key] = image;
            Order.AddLast(key);
            if (Order.Count > MaxCached)
            {
                Cache.Remove(Order.First!.Value);
                Order.RemoveFirst();
            }
            return image;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Packs several bound values into an object[] (command parameters with two arguments).</summary>
public sealed class ArrayConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => values.ToArray();

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Ratio (0..1) times the parameter (a size in pixels), for bar charts.</summary>
public sealed class ScaleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var ratio = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
        var size = System.Convert.ToDouble(parameter, CultureInfo.InvariantCulture);
        return Math.Max(2, ratio * size);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the bound count is greater than zero.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when the value is not null / empty.</summary>
public sealed class NullToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) => value != null && value is not "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>0-based alternation index to a "1." rank label.</summary>
public sealed class RankConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int i ? $"{i + 1}." : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
