using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Desktop.Wpf.Shared.Attached;

public static class KeyBehaviors
{
    private static readonly Dictionary<TextBox, PreviewKeyDownHandler> Handlers = new();

    public static readonly DependencyProperty EnterCommandProperty = DependencyProperty.RegisterAttached(
        "EnterCommand",
        typeof(ICommand),
        typeof(KeyBehaviors),
        new PropertyMetadata(null, OnEnterCommandChanged));

    public static void SetEnterCommand(DependencyObject element, ICommand? value) => element.SetValue(EnterCommandProperty, value);
    public static ICommand? GetEnterCommand(DependencyObject element) => (ICommand?)element.GetValue(EnterCommandProperty);

    public static readonly DependencyProperty AllowAltEnterNewlineProperty = DependencyProperty.RegisterAttached(
        "AllowAltEnterNewline",
        typeof(bool),
        typeof(KeyBehaviors),
        new PropertyMetadata(false));

    public static void SetAllowAltEnterNewline(DependencyObject element, bool value) => element.SetValue(AllowAltEnterNewlineProperty, value);
    public static bool GetAllowAltEnterNewline(DependencyObject element) => (bool)element.GetValue(AllowAltEnterNewlineProperty);

    private static void OnEnterCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox tb) return;

        // Remove old handler if present
        if (Handlers.TryGetValue(tb, out var existing))
        {
            tb.PreviewKeyDown -= existing.Handler;
            Handlers.Remove(tb);
        }

        if (e.NewValue is ICommand cmd)
        {
            var handler = new PreviewKeyDownHandler(tb);
            Handlers[tb] = handler;
            tb.PreviewKeyDown += handler.Handler;
        }
    }

    private sealed class PreviewKeyDownHandler
    {
        private readonly TextBox _tb;

        public PreviewKeyDownHandler(TextBox tb)
        {
            _tb = tb;
        }

        public void Handler(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;

            // Alt+Enter inserts newline when enabled
            var allowAlt = GetAllowAltEnterNewline(_tb);
            if ((Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
            {
                if (allowAlt) return;
            }

            var cmd = GetEnterCommand(_tb);
            if (cmd is null) return;

            if (cmd.CanExecute(null))
            {
                cmd.Execute(null);
                e.Handled = true;
            }
        }
    }
}
