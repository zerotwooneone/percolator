using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Desktop.Wpf.Shared.Attached;

public static class MenuBehaviors
{
    public static readonly DependencyProperty OpenOnLeftClickProperty = DependencyProperty.RegisterAttached(
        "OpenOnLeftClick",
        typeof(bool),
        typeof(MenuBehaviors),
        new PropertyMetadata(false, OnOpenOnLeftClickChanged));

    public static void SetOpenOnLeftClick(DependencyObject element, bool value) => element.SetValue(OpenOnLeftClickProperty, value);
    public static bool GetOpenOnLeftClick(DependencyObject element) => (bool)element.GetValue(OpenOnLeftClickProperty);

    private static void OnOpenOnLeftClickChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase btn) return;
        btn.Click -= OnBtnClick;
        if (e.NewValue is bool b && b)
        {
            btn.Click += OnBtnClick;
        }
    }

    private static void OnBtnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        var cm = fe.ContextMenu;
        if (cm == null) return;
        cm.PlacementTarget = fe;
        cm.Placement = PlacementMode.Bottom;
        cm.IsOpen = true;
    }

    public static readonly DependencyProperty CloseParentOnClickProperty = DependencyProperty.RegisterAttached(
        "CloseParentOnClick",
        typeof(bool),
        typeof(MenuBehaviors),
        new PropertyMetadata(false, OnCloseParentOnClickChanged));

    public static void SetCloseParentOnClick(DependencyObject element, bool value) => element.SetValue(CloseParentOnClickProperty, value);
    public static bool GetCloseParentOnClick(DependencyObject element) => (bool)element.GetValue(CloseParentOnClickProperty);

    private static void OnCloseParentOnClickChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase btn) return;
        btn.Click -= CloseMenuOnClick;
        if (e.NewValue is bool b && b)
        {
            btn.Click += CloseMenuOnClick;
        }
    }

    private static void CloseMenuOnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject d) return;
        // Find ancestor ContextMenu
        DependencyObject? current = d;
        while (current != null && current is not ContextMenu)
        {
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            if (current == null && d is FrameworkElement fe)
            {
                // Try via logical tree
                current = System.Windows.LogicalTreeHelper.GetParent(fe);
            }
        }
        if (current is ContextMenu cm)
        {
            cm.IsOpen = false;
        }
    }
}
