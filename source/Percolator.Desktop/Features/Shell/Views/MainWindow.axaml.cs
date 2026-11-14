// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/CodeBehind.spec.md before modifying this file.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using FluentAvalonia.Styling;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Shell.ViewModels;
using R3;

namespace Percolator.Desktop.Features.Shell.Views;

public partial class MainWindow : Window
{
    private const double InspectorDrawerBreakpointDip = 1000;
    private const double SidebarDrawerBreakpointDip = 850;
    private static readonly TimeSpan GeometryDebounce = TimeSpan.FromMilliseconds(400);

    private bool _chatsWasNarrow;

    // WIN-02 geometry capture state. _geometryStore is null only under the XAML previewer
    // ctor — every session path injects it, so capture/restore is armed unconditionally
    // outside the designer.
    private readonly IShellWindowGeometryStore? _geometryStore;
    private readonly Subject<bool> _geometryPersistRequests = new();
    private readonly IDisposable _geometryPersistSubscription;
    private Rect _normalBoundsDip;
    private WindowState _capturedState = WindowState.Normal;
    private WindowState _pendingRestoreState = WindowState.Normal;
    private bool _finalGeometrySaveInFlight;

    // The chats-pane state to restore when the inline column returns. Updated by
    // OnChatsSidebarPropertyChanged while the drawer is up, so user intent while
    // narrow (hamburger open/close, scrim dismiss) always wins on widening.
    private bool _sidebarOpenBeforeCollapse = true;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Opened += OnOpened;
        Closing += OnClosing;
        SizeChanged += OnSizeChanged;
        PositionChanged += OnGeometrySourceChanged;
        PropertyChanged += OnWindowPropertyChanged;
        ChatsSidebarSplitView.PropertyChanged += OnChatsSidebarPropertyChanged;

        // WIN-02: drags/resizes coalesce into one vault write per 400ms idle — the
        // debounced tick reads the fields, never the event payload, so a dropped tick
        // loses nothing ([CB-001] view mechanic; no raw dimensions reach the VM).
        _geometryPersistSubscription = _geometryPersistRequests
            .Debounce(GeometryDebounce)
            .SubscribeAwait(
                async (_, ct) => await PersistGeometryAsync(ct).ConfigureAwait(false),
                AwaitOperation.Drop);
    }

    // Complies with [CB-002] (constructor injection; parameterless ctor reserved for the XAML previewer)
    public MainWindow(MainWindowViewModel viewModel, IShellWindowGeometryStore geometryStore) : this()
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _geometryStore = geometryStore ?? throw new ArgumentNullException(nameof(geometryStore));
    }

    // Complies with [CB-003] (Loaded used for layout-dependent initialization)
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        ApplyResponsiveLayout(Bounds.Width);
        SyncThemeComboBox();
    }

    // [CB-004] — the debounce subscription lives for the window's lifetime; teardown pairs
    // with the ctor subscription.
    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        _geometryPersistSubscription.Dispose();
        _geometryPersistRequests.Dispose();
    }

    /// <summary>
    /// Applies a saved record pre-<c>Show()</c>: bounds are clamped onto the current
    /// monitors, then <see cref="WindowState"/> is deferred to <c>Opened</c> so
    /// maximization lands on the correct monitor in the same frame.
    /// </summary>
    internal void ApplyGeometry(ShellWindowGeometry? geometry)
    {
        if (geometry is null || geometry.Width <= 0 || geometry.Height <= 0)
        {
            return;
        }

        var screens = Screens.All;
        var workingAreas = new List<Rect>(screens.Count);
        foreach (var screen in screens)
        {
            var scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
            var area = screen.WorkingArea;
            workingAreas.Add(new Rect(
                area.X / scaling, area.Y / scaling, area.Width / scaling, area.Height / scaling));
        }

        var clamped = ShellWindowGeometryMath.ClampToWorkingArea(
            new Rect(geometry.X, geometry.Y, geometry.Width, geometry.Height),
            workingAreas);

        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = Math.Max(MinWidth, clamped.Width);
        Height = Math.Max(MinHeight, clamped.Height);

        // Position is a physical-pixel point; convert back with the primary screen's scaling.
        var targetScaling = screens.Count > 0 && screens[0].Scaling > 0 ? screens[0].Scaling : 1.0;
        Position = new PixelPoint(
            (int)Math.Round(clamped.X * targetScaling),
            (int)Math.Round(clamped.Y * targetScaling));

        _normalBoundsDip = clamped;
        _capturedState = geometry.State;
        _pendingRestoreState = geometry.State;
    }

    // Maximized/FullScreen apply after the window opens so the OS picks the monitor that
    // contains the restored Normal bounds; Minimized is never restored.
    private void OnOpened(object? sender, EventArgs e)
    {
        if (_pendingRestoreState is WindowState.Maximized or WindowState.FullScreen)
        {
            WindowState = _pendingRestoreState;
        }
    }

    // [CB-008] — geometry persistence is a view mechanic: the record never reaches the VM.
    private void OnGeometrySourceChanged(object? sender, EventArgs e) =>
        RequestGeometryCapture();

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty)
        {
            RequestGeometryCapture();
        }
    }

    private void RequestGeometryCapture()
    {
        if (_geometryStore is null || WindowState == WindowState.Minimized)
        {
            return;
        }

        var scaling = RenderScaling <= 0 ? 1.0 : RenderScaling;
        var boundsDip = new Rect(Position.X / scaling, Position.Y / scaling, Width, Height);
        if (ShellWindowGeometryMath.TryCapture(
                new ShellWindowGeometry(
                    _normalBoundsDip.X, _normalBoundsDip.Y,
                    _normalBoundsDip.Width, _normalBoundsDip.Height,
                    _capturedState),
                WindowState,
                boundsDip,
                out var captured)
            && captured is not null)
        {
            _normalBoundsDip = new Rect(captured.X, captured.Y, captured.Width, captured.Height);
            _capturedState = captured.State;
            _geometryPersistRequests.OnNext(true);
        }
    }

    // Async-close pattern: the debounced write may not have landed, so the final persist
    // happens here — cancel once, save, then Close() again with the guard set. No
    // sync-over-async on the UI thread; a persistence failure never aborts teardown.
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_geometryStore is null || _finalGeometrySaveInFlight)
        {
            return;
        }

        _finalGeometrySaveInFlight = true;
        e.Cancel = true;
        await PersistGeometryAsync(CancellationToken.None);
        Close();
    }

    private async Task PersistGeometryAsync(CancellationToken ct)
    {
        if (_geometryStore is null)
        {
            return;
        }

        try
        {
            await _geometryStore.SaveAsync(
                new ShellWindowGeometry(
                    _normalBoundsDip.X, _normalBoundsDip.Y,
                    _normalBoundsDip.Width, _normalBoundsDip.Height,
                    _capturedState),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Persistence failure never aborts lock/teardown — the close must proceed.
            System.Diagnostics.Debug.WriteLine($"[MainWindow] geometry persist failed: {ex}");
        }
    }

    // Complies with [CB-008] (view-only DisplayMode adaptation; no pixel values reach the ViewModel)
    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(e.NewSize.Width);
        RequestGeometryCapture();
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
