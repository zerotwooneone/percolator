// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/CodeBehind.spec.md before modifying this file.

namespace Percolator.Desktop.Features.Chats.Controls;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Percolator.Desktop.Features.Chats.ViewModels;

public partial class ChatComposer : UserControl
{
    public ChatComposer()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        ComposerInput.KeyDown += OnComposerInputKeyDown;
        ComposerInput.Focus();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        ComposerInput.KeyDown -= OnComposerInputKeyDown;
    }

    private void OnComposerInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (DataContext is ChatViewModel vm && vm.SendMessageCommand.CanExecute(null))
            {
                vm.SendMessageCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
