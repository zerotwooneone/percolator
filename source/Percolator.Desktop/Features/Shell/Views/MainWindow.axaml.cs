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
    private const double InspectorDrawerBreakpointDip = 1000;
    private const double SidebarDrawerBreakpointDip = 850;

    private bool _chatsWasNarrow;

    // The chats-pane state to restore when the inline column returns. Updated by
    // OnChatsSidebarPropertyChanged while the drawer is up, so user intent while
    // narrow (hamburger open/close, scrim dismiss) always wins on widening.
    private bool _sidebarOpenBeforeCollapse = true;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        ChatsSidebarSplitView.PropertyChanged += OnChatsSidebarPropertyChanged;
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
        // Its open state crosses both edges untouched — scrim dismiss already
        // writes back through the two-way IsPaneOpen binding.
        InspectorSplitView.DisplayMode = width < InspectorDrawerBreakpointDip
            ? SplitViewDisplayMode.Overlay
            : SplitViewDisplayMode.Inline;

        // <850px: right sidebar collapses into a slide-out drawer.
        var narrow = width < SidebarDrawerBreakpointDip;
        if (narrow == _chatsWasNarrow)
        {
            return;
        }

        if (narrow)
        {
            // Inline → Overlay: remember the pane state, then auto-close the drawer.
            // _chatsWasNarrow stays false until after the write so the pane tracker
            // doesn't record our own close as user intent.
            _sidebarOpenBeforeCollapse = ChatsSidebarSplitView.IsPaneOpen;
            ChatsSidebarSplitView.DisplayMode = SplitViewDisplayMode.Overlay;
            ChatsSidebarSplitView.IsPaneOpen = false;
            _chatsWasNarrow = true;
        }
        else
        {
            // Overlay → Inline: restore the last tracked intent — pre-collapse state,
            // or whatever the user left the drawer in while narrow — instead of
            // force-reopening a pane the user explicitly closed.
            _chatsWasNarrow = false;
            ChatsSidebarSplitView.DisplayMode = SplitViewDisplayMode.Inline;
            ChatsSidebarSplitView.IsPaneOpen = _sidebarOpenBeforeCollapse;
        }
    }

    private void OnChatsSidebarPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // Only reachable while the drawer is up (our own writes run with
        // _chatsWasNarrow already cleared/not-yet-set, so they are ignored here).
        if (_chatsWasNarrow && e.Property == SplitView.IsPaneOpenProperty)
        {
            _sidebarOpenBeforeCollapse = ChatsSidebarSplitView.IsPaneOpen;
        }
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
