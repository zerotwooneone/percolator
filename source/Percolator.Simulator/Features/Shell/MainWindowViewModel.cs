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

        toastService.Toasts
            .Subscribe(toast =>
            {
                StatusToastTitle = toast.Title;
                StatusToastMessage = toast.Message;
                IsStatusToastOpen = true;
            })
            .AddTo(ref Disposables);
    }

    [RelayCommand]
    private void DismissToast()
    {
        IsStatusToastOpen = false;
    }
}
