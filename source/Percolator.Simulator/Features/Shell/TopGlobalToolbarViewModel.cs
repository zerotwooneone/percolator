using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Features.Shell;

internal sealed partial class TopGlobalToolbarViewModel : ViewModelBase
{
    private readonly ISimulatedWorldStateService _worldState;
    private readonly IStatusToastService _toastService;
    private readonly ISimulatorLogSink _logSink;
    private readonly DesignDataService _designData;
    private bool _hasAddedRealPersona;

    public TopGlobalToolbarViewModel(
        ISimulatedWorldStateService worldState,
        IStatusToastService toastService,
        ISimulatorLogSink logSink,
        DesignDataService designData)
    {
        _worldState = worldState;
        _toastService = toastService;
        _logSink = logSink;
        _designData = designData;
    }

    [RelayCommand]
    private async Task AddPersonAsync()
    {
        // 1. If this is the first real persona added, clear Phase 1 design preview seed
        if (!_hasAddedRealPersona)
        {
            _hasAddedRealPersona = true;
            _designData.ClearPreviewData();
        }

        // 2. Generate procedural identity & allocate loopback port
        var nodeId = SimulatedNodeId.CreateNew();
        var name = WordPairNameGenerator.Generate();
        var seed = (uint)Random.Shared.Next();
        var identiconBytes = IdenticonGenerator.CreatePng(seed);
        var port = LoopbackPortAllocator.AllocateAvailablePort();

        // 3. Create and bootstrap isolated SimulatedPersonaHost
        var host = new SimulatedPersonaHost(nodeId, name, identiconBytes, port, _logSink);
        await host.StartAsync();

        // 4. Register persona in world state and pin to workbench
        var summary = new SimulatedNodeSummary(
            nodeId,
            name,
            SimulatedNodeType.Persona,
            $"127.0.0.1:{host.BoundPort} (Direct Loopback)",
            IsPinned: true);

        _worldState.RegisterPersona(summary, host);
        _worldState.TogglePinCard(summary);

        _toastService.ShowToast("Persona Spawned", $"Spawned '{name}' on port {host.BoundPort}.", ToastSeverity.Success);
    }

    [RelayCommand]
    private void BulkAddPeople() => _toastService.ShowPlaceholder("Bulk Add People", "Phase 5");

    [RelayCommand]
    private void AddRelay() => _toastService.ShowPlaceholder("Add Relay", "Phase 5");

    [RelayCommand]
    private void AdvanceOneMinute() => _toastService.ShowPlaceholder("+1 Minute", "Phase 6A");

    [RelayCommand]
    private void AdvanceOneHour() => _toastService.ShowPlaceholder("+1 Hour", "Phase 6A");

    [RelayCommand]
    private void AdvanceTwentyFourHours() => _toastService.ShowPlaceholder("+24 Hours", "Phase 8");

    [RelayCommand]
    private async Task ResetWorldAsync()
    {
        _hasAddedRealPersona = false;
        await _worldState.ClearAllAsync();
        _toastService.ShowToast("World Reset", "All simulated hosts, ports, and cards have been cleared.", ToastSeverity.Warning);
    }
}
