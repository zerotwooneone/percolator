using System.Windows;
using System.Windows.Controls;

namespace Desktop.Wpf.Shared.Controls;

public partial class InitialsAvatar : UserControl
{
    public InitialsAvatar()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(InitialsAvatar), new PropertyMetadata("?"));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly DependencyProperty IsOnlineProperty = DependencyProperty.Register(
        nameof(IsOnline), typeof(bool), typeof(InitialsAvatar), new PropertyMetadata(false));

    public bool IsOnline
    {
        get => (bool)GetValue(IsOnlineProperty);
        set => SetValue(IsOnlineProperty, value);
    }

    public static readonly DependencyProperty DiameterProperty = DependencyProperty.Register(
        nameof(Diameter), typeof(double), typeof(InitialsAvatar), new PropertyMetadata(36d));

    public double Diameter
    {
        get => (double)GetValue(DiameterProperty);
        set => SetValue(DiameterProperty, value);
    }

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(InitialsAvatar), new PropertyMetadata(new CornerRadius(18)));

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }
}
