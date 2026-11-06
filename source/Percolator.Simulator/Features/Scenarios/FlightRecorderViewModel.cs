using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ObservableCollections;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.Scenarios.Model;
using Percolator.Simulator.Features.Shell;
using R3;

namespace Percolator.Simulator.Features.Scenarios;

internal sealed partial class FlightRecorderViewModel : ViewModelBase
{
    private readonly IFlightRecorderStateService _flightRecorder;
    private readonly ScenarioRunnerService _runner;
    private readonly IStatusToastService _toastService;
    private readonly ISimulatorLogSink _logSink;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordButtonLabel))]
    private bool _isRecording;

    [ObservableProperty]
    private string? _activeReplayStatus;

    public string RecordButtonLabel => IsRecording ? "⏹️ Stop Recording" : "🔴 Record";

    public IReadOnlyObservableList<ScenarioStepDefinition> RecordedSteps => _flightRecorder.RecordedSteps;

    public FlightRecorderViewModel(
        IFlightRecorderStateService flightRecorder,
        ScenarioRunnerService runner,
        IStatusToastService toastService,
        ISimulatorLogSink logSink)
    {
        _flightRecorder = flightRecorder ?? throw new ArgumentNullException(nameof(flightRecorder));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _toastService = toastService ?? throw new ArgumentNullException(nameof(toastService));
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));

        _flightRecorder.IsRecording
            .Subscribe(v => UiTaskRunner.PostToUi(() => IsRecording = v))
            .AddTo(ref Disposables);

        _flightRecorder.ActiveReplayStatus
            .Subscribe(s => UiTaskRunner.PostToUi(() => ActiveReplayStatus = s))
            .AddTo(ref Disposables);
    }

    [RelayCommand]
    private void ToggleRecord()
    {
        _flightRecorder.ToggleRecording();
        var status = _flightRecorder.IsRecording.CurrentValue ? "Recording started" : "Recording paused";
        _toastService.ShowToast("Flight Recorder", status, ToastSeverity.Informational);
    }

    [RelayCommand]
    private void ClearSteps()
    {
        _flightRecorder.ClearSteps();
        _toastService.ShowToast("Flight Recorder", "Cleared recorded steps.", ToastSeverity.Informational);
    }

    [RelayCommand]
    private async Task SaveScenarioAsync()
    {
        if (_flightRecorder.RecordedSteps.Count == 0)
        {
            _toastService.ShowToast("Flight Recorder", "No steps to save.", ToastSeverity.Warning);
            return;
        }

        var doc = _flightRecorder.BuildScenario(
            $"Interactive Scenario {DateTime.Now:yyyyMMdd_HHmmss}",
            "Recorded interactively from Percolator Simulator workbench.");

        var scenariosDir = Path.Combine(Directory.GetCurrentDirectory(), "Scenarios");
        Directory.CreateDirectory(scenariosDir);

        var filePath = Path.Combine(scenariosDir, $"recorded-{DateTime.Now:yyyyMMdd-HHmmss}.json");

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        var json = JsonSerializer.Serialize(doc, jsonOptions);
        await File.WriteAllTextAsync(filePath, json);

        _toastService.ShowToast("Scenario Saved", $"Saved to '{Path.GetFileName(filePath)}'.", ToastSeverity.Success);
    }

    [RelayCommand]
    private async Task LoadReplayAsync()
    {
        var scenariosDir = Path.Combine(Directory.GetCurrentDirectory(), "Scenarios");
        if (!Directory.Exists(scenariosDir))
        {
            _toastService.ShowToast("Replay", "No 'Scenarios' directory found.", ToastSeverity.Warning);
            return;
        }

        var files = Directory.GetFiles(scenariosDir, "*.json").OrderBy(f => f).ToArray();
        if (files.Length == 0)
        {
            _toastService.ShowToast("Replay", "No scenario JSON files found in 'Scenarios/'.", ToastSeverity.Warning);
            return;
        }

        // Replay the first / latest scenario
        var targetFile = files.FirstOrDefault(f => f.Contains("01-direct-handshake")) ?? files.Last();
        var json = await File.ReadAllTextAsync(targetFile);

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        var doc = JsonSerializer.Deserialize<ScenarioDocument>(json, jsonOptions);
        if (doc == null)
        {
            _toastService.ShowToast("Replay Failed", "Failed to deserialize scenario.", ToastSeverity.Error);
            return;
        }

        await UiTaskRunner.RunAsync(
            () => _runner.RunScenarioAsync(doc),
            category: "ScenarioReplay",
            logSink: _logSink,
            toastService: _toastService);
    }
}
