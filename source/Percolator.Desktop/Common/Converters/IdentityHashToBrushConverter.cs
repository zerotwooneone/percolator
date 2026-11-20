using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Percolator.Desktop.Common.Converters;

/// <summary>
/// Deterministically converts an identity key string or name into a distinct, high-contrast brush for avatars.
/// </summary>
public sealed class IdentityHashToBrushConverter : IValueConverter
{
    public static readonly IdentityHashToBrushConverter Instance = new();

    private static readonly IBrush[] Palette =
    [
        new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5)), // Blue
        new SolidColorBrush(Color.FromRgb(0x43, 0xA0, 0x47)), // Green
        new SolidColorBrush(Color.FromRgb(0x8E, 0x24, 0xAA)), // Purple
        new SolidColorBrush(Color.FromRgb(0xFB, 0x8C, 0x00)), // Orange
        new SolidColorBrush(Color.FromRgb(0x00, 0x89, 0x7B)), // Teal
        new SolidColorBrush(Color.FromRgb(0x39, 0x49, 0xAB)), // Indigo
        new SolidColorBrush(Color.FromRgb(0xD8, 0x1B, 0x60)), // Pink
        new SolidColorBrush(Color.FromRgb(0x6D, 0x4C, 0x41)), // Brown
        new SolidColorBrush(Color.FromRgb(0x54, 0x6E, 0x7A)), // Blue Grey
        new SolidColorBrush(Color.FromRgb(0x00, 0xAC, 0xC1)), // Cyan
        new SolidColorBrush(Color.FromRgb(0x7C, 0xBA, 0x00)), // Lime
        new SolidColorBrush(Color.FromRgb(0x5E, 0x35, 0xB1))  // Deep Purple
    ];

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null)
        {
            return Palette[0];
        }

        var str = value.ToString() ?? string.Empty;
        var hash = (uint)str.GetHashCode();
        var index = hash % (uint)Palette.Length;
        return Palette[index];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
