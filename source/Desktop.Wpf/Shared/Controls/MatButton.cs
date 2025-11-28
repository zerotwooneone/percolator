using System.Windows;
using System.Windows.Controls;

namespace Desktop.Wpf.Shared.Controls;

public enum MatButtonVariant { Filled, Outlined }

public class MatButton : Button
{
    public static readonly DependencyProperty IsIconProperty = DependencyProperty.Register(
        nameof(IsIcon), typeof(bool), typeof(MatButton), new PropertyMetadata(false));

    public bool IsIcon
    {
        get => (bool)GetValue(IsIconProperty);
        set => SetValue(IsIconProperty, value);
    }

    public static readonly DependencyProperty VariantProperty = DependencyProperty.Register(
        nameof(Variant), typeof(MatButtonVariant), typeof(MatButton), new PropertyMetadata(MatButtonVariant.Filled));

    public MatButtonVariant Variant
    {
        get => (MatButtonVariant)GetValue(VariantProperty);
        set => SetValue(VariantProperty, value);
    }
}
