using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.Scenarios;
using Percolator.Simulator.Features.Topology;
using R3;

namespace Percolator.Simulator.Features.Shell;

internal sealed partial class MainWindowViewModel : ViewModelBase
{
    public TopGlobalToolbarViewModel Toolbar { get; }
    public ContextualActionBarViewModel ActionBar { get; }
    public RosterViewModel Roster { get; }
    public WorkbenchViewModel Workbench { get; }
    public FlightRecorderViewModel FlightRecorder { get; }
    public LogDockViewModel LogDock { get; }

    [ObservableProperty]
    private string _statusToastTitle = string.Empty;

    [ObservableProperty]
    private string _statusToastMessage = string.Empty;

    [ObservableProperty]
    private bool _isStatusToastOpen;

    [ObservableProperty]
    private bool _isPlatformMismatchWarningVisible;

    [ObservableProperty]
    private string _platformMismatchWarningMessage = string.Empty;

    public MainWindowViewModel(
        TopGlobalToolbarViewModel toolbar,
        ContextualActionBarViewModel actionBar,
        RosterViewModel roster,
        WorkbenchViewModel workbench,
        FlightRecorderViewModel flightRecorder,
        LogDockViewModel logDock,
        IStatusToastService toastService)
    {
        Toolbar = toolbar;
        ActionBar = actionBar;
        Roster = roster;
        Workbench = workbench;
        FlightRecorder = flightRecorder;
        LogDock = logDock;

#if !WINDOWS
        if (OperatingSystem.IsWindows())
        {
            IsPlatformMismatchWarningVisible = true;
            PlatformMismatchWarningMessage =
                "PLATFORM TARGET MISMATCH: Running on Windows with the generic 'net10.0' build target. " +
                "SChannel TLS 1.3 and NTFS sparse files require 'net10.0-windows10.0.19041.0'. " +
                "In Rider, switch the Run Configuration Target Framework to 'net10.0-windows10.0.19041.0'.";
        }
#endif

        toastService.Toasts
            .Subscribe(toast =>
            {
                UiTaskRunner.PostToUi(() =>
                {
                    StatusToastTitle = toast.Title;
                    StatusToastMessage = toast.Message;
                    IsStatusToastOpen = true;
                });
            })
            .AddTo(ref Disposables);
    }

    [RelayCommand]
    private void DismissToast()
    {
        IsStatusToastOpen = false;
    }

    [RelayCommand]
    private void DismissPlatformWarning()
    {
        IsPlatformMismatchWarningVisible = false;
    }
}
