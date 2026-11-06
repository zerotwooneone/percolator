using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
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

        var storageProvider = GetStorageProvider();
        var scenariosDir = ResolveScenariosDirectory();
        string? targetPath = null;

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        if (storageProvider != null && storageProvider.CanSave)
        {
            IStorageFolder? startFolder = null;
            try
            {
                startFolder = await storageProvider.TryGetFolderFromPathAsync(scenariosDir);
            }
            catch
            {
                // Ignore folder lookup failure
            }

            var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Recorded Scenario",
                DefaultExtension = "json",
                SuggestedFileName = $"scenario-{DateTime.Now:yyyyMMdd-HHmmss}.json",
                SuggestedStartLocation = startFolder,
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Scenario JSON (*.json)")
                    {
                        Patterns = new[] { "*.json" },
                        MimeTypes = new[] { "application/json" }
                    }
                }
            });

            if (file == null)
            {
                return; // User cancelled dialog
            }

            targetPath = file.TryGetLocalPath() ?? file.Path.LocalPath;
        }
        else
        {
            Directory.CreateDirectory(scenariosDir);
            targetPath = Path.Combine(scenariosDir, $"recorded-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        }

        var json = JsonSerializer.Serialize(doc, jsonOptions);
        await File.WriteAllTextAsync(targetPath, json);

        _toastService.ShowToast("Scenario Saved", $"Saved to '{Path.GetFileName(targetPath)}'.", ToastSeverity.Success);
    }

    [RelayCommand]
    private async Task LoadReplayAsync()
    {
        var storageProvider = GetStorageProvider();
        var scenariosDir = ResolveScenariosDirectory();
        string? targetFile = null;
        string? json = null;

        if (storageProvider != null && storageProvider.CanOpen)
        {
            IStorageFolder? startFolder = null;
            try
            {
                startFolder = await storageProvider.TryGetFolderFromPathAsync(scenariosDir);
            }
            catch
            {
                // Ignore folder lookup failure
            }

            var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Scenario to Replay",
                AllowMultiple = false,
                SuggestedStartLocation = startFolder,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Scenario JSON (*.json)")
                    {
                        Patterns = new[] { "*.json" },
                        MimeTypes = new[] { "application/json" }
                    },
                    new FilePickerFileType("All Files (*.*)")
                    {
                        Patterns = new[] { "*.*" }
                    }
                }
            });

            if (files == null || files.Count == 0)
            {
                return; // User cancelled dialog
            }

            var selectedFile = files[0];
            targetFile = selectedFile.TryGetLocalPath() ?? selectedFile.Path.LocalPath;

            await using var stream = await selectedFile.OpenReadAsync();
            using var reader = new StreamReader(stream);
            json = await reader.ReadToEndAsync();
        }
        else
        {
            // Fallback for headless or environments without active window
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

            targetFile = files.FirstOrDefault(f => f.Contains("01-direct-handshake")) ?? files.Last();
            json = await File.ReadAllTextAsync(targetFile);
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            _toastService.ShowToast("Replay Failed", "Selected scenario file is empty.", ToastSeverity.Error);
            return;
        }

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        ScenarioDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<ScenarioDocument>(json, jsonOptions);
        }
        catch (Exception ex)
        {
            _toastService.ShowToast("Replay Failed", $"Invalid JSON format: {ex.Message}", ToastSeverity.Error);
            return;
        }

        if (doc == null)
        {
            _toastService.ShowToast("Replay Failed", "Failed to deserialize scenario.", ToastSeverity.Error);
            return;
        }

        var name = !string.IsNullOrWhiteSpace(doc.Name) ? doc.Name : Path.GetFileName(targetFile ?? "scenario");
        _toastService.ShowToast("Scenario Replay", $"Starting replay of '{name}'...", ToastSeverity.Informational);

        await UiTaskRunner.RunAsync(
            () => _runner.RunScenarioAsync(doc),
            category: "ScenarioReplay",
            logSink: _logSink,
            toastService: _toastService);
    }

    private static IStorageProvider? GetStorageProvider()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow?.StorageProvider;
        }
        return null;
    }

    private static string ResolveScenariosDirectory()
    {
        EnsureAppDataScenariosMirrored();

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Scenarios"),
            Path.Combine(Directory.GetCurrentDirectory(), "Percolator.Simulator", "Scenarios"),
            Path.Combine(Directory.GetCurrentDirectory(), "Scenarios"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PercolatorSim", "Scenarios")
        };

        foreach (var dir in candidates)
        {
            if (Directory.Exists(dir) && Directory.GetFiles(dir, "*.json").Length > 0)
            {
                return Path.GetFullPath(dir);
            }
        }

        foreach (var dir in candidates)
        {
            if (Directory.Exists(dir))
            {
                return Path.GetFullPath(dir);
            }
        }

        var fallback = Path.Combine(AppContext.BaseDirectory, "Scenarios");
        try
        {
            Directory.CreateDirectory(fallback);
            return Path.GetFullPath(fallback);
        }
        catch
        {
            return Directory.GetCurrentDirectory();
        }
    }

    private static void EnsureAppDataScenariosMirrored()
    {
        try
        {
            var appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PercolatorSim",
                "Scenarios");

            Directory.CreateDirectory(appDataDir);

            var sourceDirs = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Scenarios"),
                Path.Combine(Directory.GetCurrentDirectory(), "Percolator.Simulator", "Scenarios")
            };

            foreach (var sDir in sourceDirs)
            {
                if (Directory.Exists(sDir))
                {
                    foreach (var file in Directory.GetFiles(sDir, "*.json"))
                    {
                        var dest = Path.Combine(appDataDir, Path.GetFileName(file));
                        if (!File.Exists(dest))
                        {
                            File.Copy(file, dest);
                        }
                    }
                }
            }
        }
        catch
        {
            // Best effort copy to AppData
        }
    }
}
