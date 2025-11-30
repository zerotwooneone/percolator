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

    public static readonly DependencyProperty IsRoundProperty = DependencyProperty.Register(
        nameof(IsRound), typeof(bool), typeof(MatButton), new PropertyMetadata(false));

    public bool IsRound
    {
        get => (bool)GetValue(IsRoundProperty);
        set => SetValue(IsRoundProperty, value);
    }

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(MatButton), new PropertyMetadata(new CornerRadius(6)));

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public static readonly DependencyProperty NotificationCountProperty = DependencyProperty.Register(
        nameof(NotificationCount), typeof(int), typeof(MatButton), new PropertyMetadata(0, OnNotificationCountChanged));

    public int NotificationCount
    {
        get => (int)GetValue(NotificationCountProperty);
        set => SetValue(NotificationCountProperty, value);
    }

    public static readonly DependencyProperty HasNotificationProperty = DependencyProperty.Register(
        nameof(HasNotification), typeof(bool), typeof(MatButton), new PropertyMetadata(false));

    public bool HasNotification
    {
        get => (bool)GetValue(HasNotificationProperty);
        set => SetValue(HasNotificationProperty, value);
    }

    private static void OnNotificationCountChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MatButton btn) return;
        var oldVal = (int)e.OldValue;
        var newVal = (int)e.NewValue;
        btn.HasNotification = newVal > 0;
        // Pulse when subsequent notifications arrive (old >= 1 and new > old)
        if (oldVal >= 1 && newVal > oldVal)
        {
            btn.TryPulseBadge();
        }
        // Also pulse when transitioning from 0 -> 1 to draw attention
        if (oldVal == 0 && newVal == 1)
        {
            btn.TryPulseBadge();
        }
    }

    private void TryPulseBadge()
    {
        if (!IsLoaded)
        {
            Loaded += (_, __) => BeginBadgePulse();
            return;
        }
        BeginBadgePulse();
    }

    private void BeginBadgePulse()
    {
        // Look for storyboard defined in ControlTemplate and retarget it explicitly to avoid name scope issues
        if (Template is null) return;
        var badge = Template.FindName("BadgeDot", this) as FrameworkElement;
        if (badge == null) return;
        var resource = this.TryFindResource("BadgePulseStoryboard") as System.Windows.Media.Animation.Storyboard;
        var sb = resource?.Clone();
        if (sb == null)
        {
            // Fallback: build a simple pulse storyboard programmatically
            sb = new System.Windows.Media.Animation.Storyboard();
            var dur = new System.Windows.Duration(System.TimeSpan.FromSeconds(0.15));
            var easeX = new System.Windows.Media.Animation.DoubleAnimation { From = 1, To = 1.4, Duration = dur, AutoReverse = true, RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(2) };
            var easeY = new System.Windows.Media.Animation.DoubleAnimation { From = 1, To = 1.4, Duration = dur, AutoReverse = true, RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(2) };
            var fade = new System.Windows.Media.Animation.DoubleAnimation { From = 1, To = 0.3, Duration = dur, AutoReverse = true, RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(2) };
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(easeX, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleX)"));
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(easeY, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(fade, new PropertyPath("Opacity"));
            sb.Children.Add(fade);
            sb.Children.Add(easeX);
            sb.Children.Add(easeY);
        }
        foreach (var tl in sb.Children)
        {
            System.Windows.Media.Animation.Storyboard.SetTarget(tl, badge);
        }
        sb.Begin();
    }
}
