using System.Globalization;
using System.Windows.Data;

namespace Desktop.Wpf.Shared.Converters;

public class DateTimeOffsetToStringConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DateTimeOffset dto)
        {
            return dto.ToString("t", culture); // Short time format
        }
        return string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
