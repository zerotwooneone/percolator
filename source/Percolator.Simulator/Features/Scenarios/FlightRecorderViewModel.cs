using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Percolator.Simulator.Features.Shell;

namespace Percolator.Simulator.Features.Scenarios;

internal sealed partial class FlightRecorderViewModel : ViewModelBase
{
    private readonly IStatusToastService _toastService;

    [ObservableProperty]
    private bool _isRecording;

    public ObservableCollection<string> RecordedSteps { get; } = new();

    public FlightRecorderViewModel(IStatusToastService toastService)
    {
        _toastService = toastService;
        RecordedSteps.Add("[Step 1] Initial Shell Seeded");
    }

    [RelayCommand]
    private void ToggleRecord()
    {
        IsRecording = !IsRecording;
        _toastService.ShowPlaceholder("Flight Recorder Toggle", "Phase 4");
    }

    [RelayCommand]
    private void ClearSteps()
    {
        RecordedSteps.Clear();
    }

    [RelayCommand]
    private void SaveScenario() => _toastService.ShowPlaceholder("Save Scenario JSON", "Phase 4");

    [RelayCommand]
    private void LoadReplay() => _toastService.ShowPlaceholder("Load & Replay Scenario", "Phase 4");
}
