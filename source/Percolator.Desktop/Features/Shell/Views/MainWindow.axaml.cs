// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/CodeBehind.spec.md before modifying this file.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using FluentAvalonia.Styling;
using Percolator.Desktop.Features.Shell.ViewModels;

namespace Percolator.Desktop.Features.Shell.Views;

public partial class MainWindow : Window
{
    private bool _chatsWasNarrow;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
    }

    // Complies with [CB-002] (constructor injection; parameterless ctor reserved for the XAML previewer)
    public MainWindow(MainWindowViewModel viewModel) : this()
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    // Complies with [CB-003] (Loaded used for layout-dependent initialization)
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        ApplyResponsiveLayout(Bounds.Width);
        SyncThemeComboBox();
    }

    // Complies with [CB-008] (view-only DisplayMode adaptation; no pixel values reach the ViewModel)
    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(e.NewSize.Width);
    }

    private void ApplyResponsiveLayout(double width)
    {
        // <1000px: inspector becomes an overlay drawer with light-dismiss scrim.
        InspectorSplitView.DisplayMode = width < 1000
            ? SplitViewDisplayMode.Overlay
            : SplitViewDisplayMode.Inline;

        // <850px: right sidebar collapses into a slide-out drawer.
        var narrow = width < 850;
        if (narrow == _chatsWasNarrow)
        {
            return;
        }

        _chatsWasNarrow = narrow;
        ChatsSidebarSplitView.DisplayMode = narrow
            ? SplitViewDisplayMode.Overlay
            : SplitViewDisplayMode.Inline;

        // Close the pane when collapsing to overlay; restore it when the inline column returns.
        ChatsSidebarSplitView.IsPaneOpen = !narrow;
    }

    // Complies with [CB-001] (theme switching is a pure view mechanic)
    private void ThemeComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Avalonia.Application.Current is null || ThemeComboBox.SelectedIndex < 0)
        {
            return;
        }

        Avalonia.Application.Current.RequestedThemeVariant = ThemeComboBox.SelectedIndex switch
        {
            1 => ThemeVariant.Light,
            2 => FluentAvaloniaTheme.HighContrastTheme,
            _ => ThemeVariant.Dark
        };
    }

    private void SyncThemeComboBox()
    {
        var actual = Avalonia.Application.Current?.ActualThemeVariant;
        ThemeComboBox.SelectedIndex = actual switch
        {
            _ when actual == ThemeVariant.Light => 1,
            _ when actual == FluentAvaloniaTheme.HighContrastTheme => 2,
            _ => 0
        };
    }
}
