using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Percolator.Apps.FileTransfer.Domain.SwarmCoordination;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.Scenarios;
using Percolator.Simulator.Features.Scenarios.Model;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;
using R3;

namespace Percolator.Simulator.Features.SwarmSimulation;

internal sealed partial class SwarmParticipantViewModel : ViewModelBase
{
    public SimulatedNodeId NodeId { get; }

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private string _roleText;

    [ObservableProperty]
    private IReadOnlyList<PieceState> _pieces;

    [ObservableProperty]
    private int _completedChunks;

    [ObservableProperty]
    private int _totalChunks;

    [ObservableProperty]
    private string _progressText = string.Empty;

    public SwarmParticipantViewModel(
        SimulatedNodeId nodeId,
        string displayName,
        string roleText,
        IReadOnlyList<PieceState> pieces,
        int completedChunks,
        int totalChunks,
        string progressText)
    {
        NodeId = nodeId;
        _displayName = displayName;
        _roleText = roleText;
        _pieces = pieces;
        _completedChunks = completedChunks;
        _totalChunks = totalChunks;
        _progressText = progressText;
    }

    public void UpdateProgress(IReadOnlyList<PieceState> newPieces, int completed, int total, double speed)
    {
        Pieces = newPieces.ToArray();
        CompletedChunks = completed;
        TotalChunks = total;
        double pct = total > 0 ? (double)completed / total * 100.0 : 0.0;
        string speedMb = speed > 0 ? $" • {speed / (1024 * 1024):F1} MB/s" : "";
        ProgressText = $"{completed}/{total} chunks ({pct:F0}%){speedMb}";
    }
}

internal sealed partial class SwarmPanelViewModel : ViewModelBase
{
    private readonly SimulatedNodeSummary _summary;
    private readonly IStatusToastService _toastService;
    private readonly ISimulatorStateBus _stateBus;
    private readonly ISimulatedWorldStateService _worldState;
    private readonly SwarmSimulationCoordinator _swarmCoordinator;
    private readonly ISimulatorLogSink _logSink;

    private ActiveSwarmState? _swarmState;

    public SimulatedNodeId NodeId => _summary.Id;

    [ObservableProperty]
    private string _swarmTitle;

    [ObservableProperty]
    private string _statusText = "Active Transfer";

    [ObservableProperty]
    private string _fileInfoText = string.Empty;

    [ObservableProperty]
    private int _totalPieces;

    public ObservableCollection<SwarmParticipantViewModel> Participants { get; } = new();

    public SwarmPanelViewModel(
        SimulatedNodeSummary summary,
        IStatusToastService toastService,
        ISimulatorStateBus stateBus,
        ISimulatedWorldStateService worldState,
        SwarmSimulationCoordinator swarmCoordinator,
        ISimulatorLogSink logSink)
    {
        _summary = summary ?? throw new ArgumentNullException(nameof(summary));
        _toastService = toastService ?? throw new ArgumentNullException(nameof(toastService));
        _stateBus = stateBus ?? throw new ArgumentNullException(nameof(stateBus));
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _swarmCoordinator = swarmCoordinator ?? throw new ArgumentNullException(nameof(swarmCoordinator));
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));

        _swarmTitle = summary.DisplayName;
        _swarmState = _swarmCoordinator.GetSwarmByNodeId(_summary.Id) ?? _swarmCoordinator.GetLatestSwarm();

        InitializeFromState();

        // Subscribe to live swarm progress signals throttled at 33ms (~30 FPS)
        _stateBus.Signals
            .Where(s => s.Category == SignalCategory.SwarmProgressChanged || s.Category == SignalCategory.FileTransferCompleted)
            .ThrottleLast(TimeSpan.FromMilliseconds(33))
            .Subscribe(_ =>
            {
                UiTaskRunner.PostToUi(RefreshProgress);
            })
            .AddTo(ref Disposables);
    }

    private void InitializeFromState()
    {
        _swarmState = _swarmCoordinator.GetSwarmByNodeId(_summary.Id) ?? _swarmCoordinator.GetLatestSwarm();
        if (_swarmState == null) return;

        SwarmTitle = $"🌐 Swarm: {_swarmState.Preset.RelativeFileName}";
        FileInfoText = $"Total: {_swarmState.Preset.TotalBytes:N0} bytes • {_swarmState.TotalChunks} chunks ({_swarmState.ChunkSize / 1024} KB each)";
        TotalPieces = _swarmState.TotalChunks;

        Participants.Clear();
        foreach (var p in _swarmState.Participants)
        {
            string role = p.Role == SwarmPeerRole.Seeder ? "Seeder (100%)" : "Downloader";
            var vm = new SwarmParticipantViewModel(
                p.NodeId,
                p.DisplayName,
                role,
                p.Pieces.ToArray(),
                p.CompletedChunks,
                p.TotalChunks,
                $"{p.CompletedChunks}/{p.TotalChunks} chunks");
            Participants.Add(vm);
        }
    }

    private void RefreshProgress()
    {
        if (_swarmState == null)
        {
            InitializeFromState();
            return;
        }

        // Check for any newly injected participants (e.g. late joiner)
        foreach (var p in _swarmState.Participants)
        {
            var existing = Participants.FirstOrDefault(vm => vm.NodeId == p.NodeId);
            if (existing == null)
            {
                string role = p.Role == SwarmPeerRole.Seeder ? "Seeder (100%)" : "Late Downloader";
                var newVm = new SwarmParticipantViewModel(
                    p.NodeId,
                    p.DisplayName,
                    role,
                    p.Pieces.ToArray(),
                    p.CompletedChunks,
                    p.TotalChunks,
                    $"{p.CompletedChunks}/{p.TotalChunks} chunks");
                Participants.Add(newVm);
            }
            else
            {
                existing.UpdateProgress(p.Pieces, p.CompletedChunks, p.TotalChunks, p.SpeedBytesPerSec);
            }
        }

        bool allDone = _swarmState.Participants.All(p => p.IsComplete);
        if (allDone)
        {
            StatusText = "Transfer Completed & Verified ✓";
        }
        else if (_swarmState.Participants.Any(p => p.IsPaused))
        {
            StatusText = "Transfer Paused ⏸️";
        }
        else if (_swarmState.Participants.Any(p => p.IsCancelled))
        {
            StatusText = "Transfer Cancelled ✕";
        }
        else
        {
            StatusText = "Active Swarm Transfer (30 FPS Pieces)";
        }
    }

    [RelayCommand]
    private async Task PauseTransferAsync()
    {
        if (_swarmState == null) return;
        await _swarmCoordinator.PauseTransferAsync(_swarmState.SessionId);
        StatusText = "Transfer Paused ⏸️";
        _toastService.ShowToast("Transfer Paused", "Swarm chunk requests paused.", ToastSeverity.Informational);
    }

    [RelayCommand]
    private async Task ResumeTransferAsync()
    {
        if (_swarmState == null) return;
        await _swarmCoordinator.ResumeTransferAsync(_swarmState.SessionId);
        StatusText = "Transfer Resumed ▶️";
        _toastService.ShowToast("Transfer Resumed", "Swarm chunk requests resumed.", ToastSeverity.Informational);
    }

    [RelayCommand]
    private async Task CancelTransferAsync()
    {
        if (_swarmState == null) return;
        await _swarmCoordinator.CancelTransferAsync(_swarmState.SessionId);
        StatusText = "Transfer Cancelled ✕";
        _toastService.ShowToast("Transfer Cancelled", "Swarm transfer terminated.", ToastSeverity.Warning);
    }

    [RelayCommand]
    private async Task InjectLateDownloaderAsync()
    {
        if (_swarmState == null)
        {
            _toastService.ShowToast("No Active Swarm", "Initiate a file transfer preset first.", ToastSeverity.Warning);
            return;
        }

        // Find personas not already in the swarm
        var existingIds = _swarmState.Participants.Select(p => p.NodeId).ToHashSet();
        var candidate = _worldState.Personas.FirstOrDefault(p => !existingIds.Contains(p.Id));

        if (candidate == null)
        {
            _toastService.ShowToast("No Persona Available", "Spawn another persona (e.g. '+ Add Person') to inject as late downloader.", ToastSeverity.Warning);
            return;
        }

        bool success = await _swarmCoordinator.InjectLateDownloaderAsync(_swarmState.SessionId, candidate.Id, progressThreshold: 0.5);
        if (success)
        {
            _toastService.ShowToast("Late Downloader Scheduled", $"Injected '{candidate.DisplayName}' into swarm (triggers at 50% progress).", ToastSeverity.Success);
            RefreshProgress();
        }
    }
}
