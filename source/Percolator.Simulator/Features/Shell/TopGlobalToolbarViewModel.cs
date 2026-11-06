using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.RelayedChat;
using Percolator.Simulator.Features.Scenarios;
using Percolator.Simulator.Features.Scenarios.Model;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Features.Shell;

internal sealed partial class TopGlobalToolbarViewModel : ViewModelBase
{
    private readonly ISimulatedWorldStateService _worldState;
    private readonly ISelectionStateService _selectionState;
    private readonly IStatusToastService _toastService;
    private readonly ISimulatorLogSink _logSink;
    private readonly ISimulatorStateBus _stateBus;
    private readonly DesignDataService _designData;
    private readonly IFlightRecorderStateService _flightRecorder;
    private readonly ScenarioRunnerService _scenarioRunner;
    private bool _hasAddedRealPersona;

    public TopGlobalToolbarViewModel(
        ISimulatedWorldStateService worldState,
        ISelectionStateService selectionState,
        IStatusToastService toastService,
        ISimulatorLogSink logSink,
        ISimulatorStateBus stateBus,
        DesignDataService designData,
        IFlightRecorderStateService flightRecorder,
        ScenarioRunnerService scenarioRunner)
    {
        _worldState = worldState;
        _selectionState = selectionState;
        _toastService = toastService;
        _logSink = logSink;
        _stateBus = stateBus;
        _designData = designData;
        _flightRecorder = flightRecorder;
        _scenarioRunner = scenarioRunner;
    }

    [RelayCommand]
    private async Task AddPersonAsync()
    {
        // 1. If this is the first real persona added, clear Phase 1 design preview seed on UI thread
        if (!_hasAddedRealPersona)
        {
            _hasAddedRealPersona = true;
            _designData.ClearPreviewData();
        }

        // 2. Offload key generation, identicon rendering, port allocation, SQLCipher DB init, and Kestrel startup to background thread
        var host = await UiTaskRunner.RunAsync(
            async () =>
            {
                var nodeId = SimulatedNodeId.CreateNew();
                var name = WordPairNameGenerator.Generate();
                var seed = (uint)Random.Shared.Next();
                var identiconBytes = IdenticonGenerator.CreatePng(seed);
                var port = LoopbackPortAllocator.AllocateAvailablePort();

                var personaHost = new SimulatedPersonaHost(nodeId, name, identiconBytes, port, _logSink, _stateBus, _worldState);
                await personaHost.StartAsync();
                return personaHost;
            },
            category: "HostLifecycle",
            logSink: _logSink,
            toastService: _toastService);

        if (host is null) return;

        // 3. Register persona in world state and pin to workbench on UI thread
        var summary = new SimulatedNodeSummary(
            host.Id,
            host.DisplayName,
            SimulatedNodeType.Persona,
            $"127.0.0.1:{host.BoundPort} (Direct Loopback)",
            IsPinned: true);

        _worldState.RegisterPersona(summary, host);
        _worldState.TogglePinCard(summary);

        // Record interactive step in flight recorder if active
        _flightRecorder.RecordStep(new SpawnPersonasStep(new[] { host.DisplayName }));

        // 4. Auto-assign to Primary (1°) or Secondary (2°) selection slot if empty
        if (_selectionState.PrimarySelectedNode.CurrentValue is null)
        {
            _selectionState.SetPrimary(summary);
        }
        else if (_selectionState.SecondarySelectedNode.CurrentValue is null &&
                 _selectionState.PrimarySelectedNode.CurrentValue.Id != summary.Id)
        {
            _selectionState.SetSecondary(summary);
        }

        _toastService.ShowToast("Persona Spawned", $"Spawned '{host.DisplayName}' on port {host.BoundPort}.", ToastSeverity.Success);
    }

    [RelayCommand]
    private async Task BulkAddPeopleAsync()
    {
        if (!_hasAddedRealPersona)
        {
            _hasAddedRealPersona = true;
            _designData.ClearPreviewData();
        }

        var spawnedHosts = await UiTaskRunner.RunAsync(
            async () =>
            {
                var list = new List<ISimulatedPersonaHost>();
                for (int i = 0; i < 3; i++)
                {
                    var nodeId = SimulatedNodeId.CreateNew();
                    var name = WordPairNameGenerator.Generate();
                    var seed = (uint)Random.Shared.Next();
                    var identiconBytes = IdenticonGenerator.CreatePng(seed);
                    var port = LoopbackPortAllocator.AllocateAvailablePort();

                    var personaHost = new SimulatedPersonaHost(nodeId, name, identiconBytes, port, _logSink, _stateBus, _worldState);
                    await personaHost.StartAsync();
                    list.Add(personaHost);
                }
                return list;
            },
            category: "HostLifecycle",
            logSink: _logSink,
            toastService: _toastService);

        if (spawnedHosts == null || spawnedHosts.Count == 0) return;

        var names = new List<string>();
        foreach (var host in spawnedHosts)
        {
            var summary = new SimulatedNodeSummary(
                host.Id,
                host.DisplayName,
                SimulatedNodeType.Persona,
                $"127.0.0.1:{host.BoundPort} (Direct Loopback)",
                IsPinned: true);

            _worldState.RegisterPersona(summary, host);
            _worldState.TogglePinCard(summary);
            names.Add(host.DisplayName);
        }

        _flightRecorder.RecordStep(new SpawnPersonasStep(names));

        _toastService.ShowToast("Bulk Personas Spawned", $"Spawned {spawnedHosts.Count} personas.", ToastSeverity.Success);
    }

    [RelayCommand]
    private async Task AddRelayAsync()
    {
        if (!_hasAddedRealPersona)
        {
            _hasAddedRealPersona = true;
            _designData.ClearPreviewData();
        }

        var relay = await UiTaskRunner.RunAsync(
            async () =>
            {
                var nodeId = SimulatedNodeId.CreateNew();
                var name = $"Relay-{_worldState.Relays.Count + 1}";
                var port = LoopbackPortAllocator.AllocateAvailablePort();

                var host = new SimulatedRelayHost(nodeId, name, port, _logSink, _stateBus);
                await host.StartAsync();
                return host;
            },
            category: "HostLifecycle",
            logSink: _logSink,
            toastService: _toastService);

        if (relay is null) return;

        var summary = new SimulatedNodeSummary(
            relay.Id,
            relay.DisplayName,
            SimulatedNodeType.Relay,
            $"127.0.0.1:{relay.BoundPort} (Relay)",
            IsPinned: true);

        _worldState.RegisterRelay(summary, relay);
        _worldState.TogglePinCard(summary);

        _flightRecorder.RecordStep(new SpawnRelayStep(relay.DisplayName));

        _toastService.ShowToast("Relay Spawned", $"Spawned '{relay.DisplayName}' on port {relay.BoundPort}.", ToastSeverity.Success);
    }

    [RelayCommand]
    private async Task RunScenarioAsync()
    {
        var scenariosDir = Path.Combine(Directory.GetCurrentDirectory(), "Scenarios");
        var defaultScenarioPath = Path.Combine(scenariosDir, "01-direct-handshake-and-chat.json");
        if (!File.Exists(defaultScenarioPath))
        {
            _toastService.ShowToast("Scenario Missing", "01-direct-handshake-and-chat.json not found.", ToastSeverity.Warning);
            return;
        }

        var json = await File.ReadAllTextAsync(defaultScenarioPath);
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        var doc = JsonSerializer.Deserialize<ScenarioDocument>(json, jsonOptions);
        if (doc != null)
        {
            await UiTaskRunner.RunAsync(
                () => _scenarioRunner.RunScenarioAsync(doc),
                category: "ScenarioReplay",
                logSink: _logSink,
                toastService: _toastService);
        }
    }

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
        _selectionState.SetPrimary(null);
        _selectionState.SetSecondary(null);
        _selectionState.ClearMultiSelection();

        await UiTaskRunner.RunAsync(
            () => _worldState.ClearAllAsync(),
            category: "HostLifecycle",
            logSink: _logSink,
            toastService: _toastService);

        _toastService.ShowToast("World Reset", "All active hosts and loopback ports released.", ToastSeverity.Informational);
    }
}
