using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.RichChatAndProfiles;
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
    private readonly ISimulatedNodeFactory _nodeFactory;
    private readonly IVirtualClockStateService _virtualClock;
    private bool _hasAddedRealPersona;

    public TopGlobalToolbarViewModel(
        ISimulatedWorldStateService worldState,
        ISelectionStateService selectionState,
        IStatusToastService toastService,
        ISimulatorLogSink logSink,
        ISimulatorStateBus stateBus,
        DesignDataService designData,
        IFlightRecorderStateService flightRecorder,
        ScenarioRunnerService scenarioRunner,
        ISimulatedNodeFactory nodeFactory,
        IVirtualClockStateService virtualClock)
    {
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _selectionState = selectionState ?? throw new ArgumentNullException(nameof(selectionState));
        _toastService = toastService ?? throw new ArgumentNullException(nameof(toastService));
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
        _stateBus = stateBus ?? throw new ArgumentNullException(nameof(stateBus));
        _designData = designData ?? throw new ArgumentNullException(nameof(designData));
        _flightRecorder = flightRecorder ?? throw new ArgumentNullException(nameof(flightRecorder));
        _scenarioRunner = scenarioRunner ?? throw new ArgumentNullException(nameof(scenarioRunner));
        _nodeFactory = nodeFactory ?? throw new ArgumentNullException(nameof(nodeFactory));
        _virtualClock = virtualClock ?? throw new ArgumentNullException(nameof(virtualClock));
    }

    [RelayCommand]
    private async Task AddPersonAsync()
    {
        // 1. If this is the first real persona added, clear design preview seed on UI thread
        if (!_hasAddedRealPersona)
        {
            _hasAddedRealPersona = true;
            _designData.ClearPreviewData();
        }

        // 2. Offload persona generation to shared node factory on background thread
        var host = await UiTaskRunner.RunAsync(
            () => _nodeFactory.CreatePersonaHostAsync(),
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
                    list.Add(await _nodeFactory.CreatePersonaHostAsync());
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
            () => _nodeFactory.CreateRelayHostAsync(),
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
    private void AdvanceOneMinute()
    {
        _virtualClock.AdvanceTime(TimeSpan.FromMinutes(1));
        _flightRecorder.RecordStep(new AdvanceVirtualTimeStep(60));
        _toastService.ShowToast("Virtual Clock", "Advanced virtual time by +1 Minute.", ToastSeverity.Informational);
    }

    [RelayCommand]
    private void AdvanceOneHour()
    {
        _virtualClock.AdvanceTime(TimeSpan.FromHours(1));
        _flightRecorder.RecordStep(new AdvanceVirtualTimeStep(3600));
        _toastService.ShowToast("Virtual Clock", "Advanced virtual time by +1 Hour.", ToastSeverity.Informational);
    }

    [RelayCommand]
    private void AdvanceTwentyFourHours()
    {
        _virtualClock.AdvanceTime(TimeSpan.FromHours(24));
        _flightRecorder.RecordStep(new AdvanceVirtualTimeStep(86400));
        _toastService.ShowToast("Virtual Clock", "Advanced virtual time by +24 Hours (Discovery Epoch rotated).", ToastSeverity.Informational);
    }

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
