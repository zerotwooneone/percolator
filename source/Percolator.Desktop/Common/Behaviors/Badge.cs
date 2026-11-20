using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Percolator.Desktop.Common.Behaviors;

/// <summary>
/// Attached properties for rendering notification badges and pulse animations on buttons, navigation items, and icons.
/// </summary>
public static class Badge
{
    public static readonly AttachedProperty<int> CountProperty =
        AvaloniaProperty.RegisterAttached<Control, int>("Count", typeof(Badge), 0);

    public static readonly AttachedProperty<bool> HasBadgeProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("HasBadge", typeof(Badge), false);

    public static readonly AttachedProperty<bool> PulseProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Pulse", typeof(Badge), false);

    public static readonly AttachedProperty<IBrush?> BadgeBrushProperty =
        AvaloniaProperty.RegisterAttached<Control, IBrush?>("BadgeBrush", typeof(Badge));

    public static int GetCount(Control element) => element.GetValue(CountProperty);
    public static void SetCount(Control element, int value) => element.SetValue(CountProperty, value);

    public static bool GetHasBadge(Control element) => element.GetValue(HasBadgeProperty);
    public static void SetHasBadge(Control element, bool value) => element.SetValue(HasBadgeProperty, value);

    public static bool GetPulse(Control element) => element.GetValue(PulseProperty);
    public static void SetPulse(Control element, bool value) => element.SetValue(PulseProperty, value);

    public static IBrush? GetBadgeBrush(Control element) => element.GetValue(BadgeBrushProperty);
    public static void SetBadgeBrush(Control element, IBrush? value) => element.SetValue(BadgeBrushProperty, value);
}
