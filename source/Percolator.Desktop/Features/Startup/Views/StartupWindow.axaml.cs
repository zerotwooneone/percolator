// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/CodeBehind.spec.md before modifying this file.

using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Percolator.Desktop.Features.Startup.ViewModels;

namespace Percolator.Desktop.Features.Startup.Views;

public partial class StartupWindow : Window
{
    public StartupWindow()
    {
        InitializeComponent();
    }

    // Complies with [CB-002] (constructor injection; parameterless ctor reserved for the XAML previewer)
    public StartupWindow(StartupHostViewModel viewModel) : this()
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        Loaded += OnLoaded;
    }

    // Complies with [CB-003] (Loaded used for the one-time startup sequence)
    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is StartupHostViewModel vm)
        {
            await vm.InitializeAsync();
        }
    }
}
