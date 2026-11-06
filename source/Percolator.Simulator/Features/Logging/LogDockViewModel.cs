using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Percolator.Simulator.Features.Shell;

namespace Percolator.Simulator.Features.Logging;

internal sealed partial class LogDockViewModel : ViewModelBase
{
    private readonly IStatusToastService _toastService;

    [ObservableProperty]
    private int _selectedLogLevelIndex = 2; // Default Information

    public ObservableCollection<string> LogEntries { get; } = new();

    public LogDockViewModel(IStatusToastService toastService)
    {
        _toastService = toastService;

        // Seed initial log entries
        LogEntries.Add("[INFO] [Shell] Percolator Simulator initialized in UI Preview Mode.");
        LogEntries.Add("[INFO] [Shell] 4-zone UI shell active. Logging engine scheduled for Phase 2.");
    }

    [RelayCommand]
    private void ClearLog()
    {
        LogEntries.Clear();
    }
}
