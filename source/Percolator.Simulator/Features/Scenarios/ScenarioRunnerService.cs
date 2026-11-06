using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Application2.Ports;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Simulator.Features.DirectChat;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.Scenarios.Model;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;
using R3;

namespace Percolator.Simulator.Features.Scenarios;

internal sealed class ScenarioRunnerService
{
    private readonly ISimulatedWorldStateService _worldState;
    private readonly ISimulatorStateBus _stateBus;
    private readonly DirectHandshakeOrchestrator _handshakeOrchestrator;
    private readonly DirectChatCoordinator _chatCoordinator;
    private readonly ISimulatorLogSink _logSink;
    private readonly IFlightRecorderStateService? _flightRecorder;
    private readonly ISelectionStateService? _selectionState;

    public ScenarioRunnerService(
        ISimulatedWorldStateService worldState,
        ISimulatorStateBus stateBus,
        DirectHandshakeOrchestrator handshakeOrchestrator,
        DirectChatCoordinator chatCoordinator,
        ISimulatorLogSink logSink,
        IFlightRecorderStateService? flightRecorder = null,
        ISelectionStateService? selectionState = null)
    {
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _stateBus = stateBus ?? throw new ArgumentNullException(nameof(stateBus));
        _handshakeOrchestrator = handshakeOrchestrator ?? throw new ArgumentNullException(nameof(handshakeOrchestrator));
        _chatCoordinator = chatCoordinator ?? throw new ArgumentNullException(nameof(chatCoordinator));
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
        _flightRecorder = flightRecorder;
        _selectionState = selectionState;
    }

    public async Task RunScenarioAsync(ScenarioDocument scenario, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        _logSink.Log(new SimulatorLogEntry(
            DateTimeOffset.UtcNow,
            LogLevel.Information,
            "ScenarioRunner",
            null,
            "Scenario",
            $"=== Starting Scenario Replay: '{scenario.Name}' ({scenario.Steps.Count} steps, {scenario.Assertions.Count} assertions) ==="));

        _flightRecorder?.SetReplayStatus($"Replaying: '{scenario.Name}'...");

        var errorLogs = new List<SimulatorLogEntry>();
        using var logSub = _logSink.LogStream
            .Where(l => l.Level >= LogLevel.Error)
            .Subscribe(entry =>
            {
                lock (errorLogs)
                {
                    errorLogs.Add(entry);
                }
            });

        // 1. Clean slate reset
        await _worldState.ClearAllAsync();
        if (_selectionState != null)
        {
            _selectionState.SetPrimary(null);
            _selectionState.SetSecondary(null);
            _selectionState.ClearMultiSelection();
        }
        var aliasMap = new Dictionary<string, ISimulatedPersonaHost>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // 2. Execute Steps
            for (int i = 0; i < scenario.Steps.Count; i++)
            {
                var step = scenario.Steps[i];
                var stepNum = i + 1;
                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Information,
                    "ScenarioRunner",
                    null,
                    "Scenario",
                    $"[Step {stepNum}/{scenario.Steps.Count}] {step.DisplayText}"));

                _flightRecorder?.SetReplayStatus($"Step {stepNum}/{scenario.Steps.Count}: {step.DisplayText}");

                switch (step)
                {
                    case SpawnPersonasStep spawnStep:
                        await ExecuteSpawnPersonasAsync(spawnStep, aliasMap, ct);
                        break;

                    case InitiateDirectHandshakeStep handshakeStep:
                        await ExecuteInitiateDirectHandshakeAsync(handshakeStep, aliasMap, ct);
                        break;

                    case ApprovePendingHandshakeStep approveStep:
                        await ExecuteApprovePendingHandshakeAsync(approveStep, aliasMap, ct);
                        break;

                    case RejectPendingHandshakeStep rejectStep:
                        await ExecuteRejectPendingHandshakeAsync(rejectStep, aliasMap, ct);
                        break;

                    case SetListeningStateStep listeningStep:
                        ExecuteSetListeningState(listeningStep, aliasMap);
                        break;

                    case SendDirectMessageStep messageStep:
                        await ExecuteSendDirectMessageAsync(messageStep, aliasMap, ct);
                        break;

                    default:
                        throw new ScenarioExecutionException($"Unsupported scenario step type: {step.GetType().Name}");
                }
            }

            // 3. Execute Assertions
            for (int i = 0; i < scenario.Assertions.Count; i++)
            {
                var assertion = scenario.Assertions[i];
                var assertNum = i + 1;
                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Information,
                    "ScenarioRunner",
                    null,
                    "Scenario",
                    $"[Assert {assertNum}/{scenario.Assertions.Count}] {assertion.DisplayText}"));

                _flightRecorder?.SetReplayStatus($"Verifying {assertNum}/{scenario.Assertions.Count}: {assertion.DisplayText}");

                switch (assertion)
                {
                    case AssertDirectRatchetSessionAssertion ratchetAssert:
                        await ExecuteAssertDirectRatchetSessionAsync(ratchetAssert, aliasMap, ct);
                        break;

                    case AssertMessageDeliveredAssertion messageAssert:
                        await ExecuteAssertMessageDeliveredAsync(messageAssert, aliasMap, ct);
                        break;

                    case AssertZeroErrorsAssertion:
                        ExecuteAssertZeroErrors(errorLogs);
                        break;

                    default:
                        throw new ScenarioExecutionException($"Unsupported scenario assertion type: {assertion.GetType().Name}");
                }
            }

            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Information,
                "ScenarioRunner",
                null,
                "Scenario",
                $"=== Scenario Replay PASSED: '{scenario.Name}' ==="));

            _flightRecorder?.SetReplayStatus($"PASS: '{scenario.Name}'");
        }
        catch (Exception ex)
        {
            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Error,
                "ScenarioRunner",
                null,
                "Scenario",
                $"=== Scenario Replay FAILED: '{scenario.Name}' - {ex.Message} ===",
                ex.ToString()));

            _flightRecorder?.SetReplayStatus($"FAILED: {ex.Message}");
            throw;
        }
    }

    private async Task ExecuteSpawnPersonasAsync(
        SpawnPersonasStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        CancellationToken ct)
    {
        foreach (var alias in step.Aliases)
        {
            var nodeId = SimulatedNodeId.CreateNew();
            var seed = (uint)Random.Shared.Next();
            var identiconBytes = IdenticonGenerator.CreatePng(seed);
            var port = LoopbackPortAllocator.AllocateAvailablePort();

            var host = new SimulatedPersonaHost(nodeId, alias, identiconBytes, port, _logSink, _stateBus, _worldState);
            await host.StartAsync(ct);

            var summary = new SimulatedNodeSummary(
                host.Id,
                host.DisplayName,
                SimulatedNodeType.Persona,
                $"127.0.0.1:{host.BoundPort} (Direct Loopback)",
                IsPinned: true);

            _worldState.RegisterPersona(summary, host);
            _worldState.TogglePinCard(summary);
            aliasMap[alias] = host;

            if (_selectionState != null)
            {
                if (_selectionState.PrimarySelectedNode.CurrentValue is null)
                {
                    _selectionState.SetPrimary(summary);
                }
                else if (_selectionState.SecondarySelectedNode.CurrentValue is null &&
                         _selectionState.PrimarySelectedNode.CurrentValue.Id != summary.Id)
                {
                    _selectionState.SetSecondary(summary);
                }
            }
        }
    }

    private async Task ExecuteInitiateDirectHandshakeAsync(
        InitiateDirectHandshakeStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        CancellationToken ct)
    {
        var initiator = GetHostOrThrow(step.InitiatorAlias, aliasMap);
        var target = GetHostOrThrow(step.TargetAlias, aliasMap);

        using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stepCts.CancelAfter(TimeSpan.FromSeconds(10));

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = stepCts.Token.Register(() => tcs.TrySetCanceled(stepCts.Token));

        using var sub = _stateBus.Signals
            .Where(s => s.NodeId == target.Id && s.Category == SignalCategory.PendingHandshakeChanged)
            .Subscribe(_ => tcs.TrySetResult(true));

        var success = await _handshakeOrchestrator.InitiateDirectHandshakeAsync(initiator.Id, target.Id, stepCts.Token);
        if (!success)
        {
            throw new ScenarioExecutionException($"InitiateDirectHandshakeAsync from '{step.InitiatorAlias}' to '{step.TargetAlias}' failed.");
        }

        try
        {
            await tcs.Task;
        }
        catch (OperationCanceledException) when (stepCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out after 10s waiting for target '{step.TargetAlias}' to receive pending handshake.");
        }
    }

    private async Task ExecuteApprovePendingHandshakeAsync(
        ApprovePendingHandshakeStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        CancellationToken ct)
    {
        var owner = GetHostOrThrow(step.OwnerAlias, aliasMap);
        var sender = GetHostOrThrow(step.SenderAlias, aliasMap);

        using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stepCts.CancelAfter(TimeSpan.FromSeconds(10));

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = stepCts.Token.Register(() => tcs.TrySetCanceled(stepCts.Token));

        using var sub = _stateBus.Signals
            .Where(s => s.Category == SignalCategory.RatchetSessionChanged && (s.NodeId == owner.Id || s.NodeId == sender.Id))
            .Subscribe(_ => tcs.TrySetResult(true));

        var success = await _handshakeOrchestrator.AcceptHandshakeAsync(owner.Id, sender.PublicIdentityId, stepCts.Token);
        if (!success)
        {
            throw new ScenarioExecutionException($"AcceptHandshakeAsync for '{step.OwnerAlias}' approving '{step.SenderAlias}' failed.");
        }

        try
        {
            await tcs.Task;
        }
        catch (OperationCanceledException) when (stepCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out after 10s waiting for RatchetSessionChanged after '{step.OwnerAlias}' approved '{step.SenderAlias}'.");
        }
    }

    private async Task ExecuteRejectPendingHandshakeAsync(
        RejectPendingHandshakeStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        CancellationToken ct)
    {
        var owner = GetHostOrThrow(step.OwnerAlias, aliasMap);
        var sender = GetHostOrThrow(step.SenderAlias, aliasMap);

        var success = await _handshakeOrchestrator.RejectHandshakeAsync(owner.Id, sender.PublicIdentityId, step.Block, ct);
        if (!success)
        {
            throw new ScenarioExecutionException($"RejectHandshakeAsync for '{step.OwnerAlias}' rejecting '{step.SenderAlias}' failed.");
        }
    }

    private static void ExecuteSetListeningState(
        SetListeningStateStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap)
    {
        var host = GetHostOrThrow(step.Alias, aliasMap);
        host.SetListening(step.IsListening);
    }

    private async Task ExecuteSendDirectMessageAsync(
        SendDirectMessageStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        CancellationToken ct)
    {
        var sender = GetHostOrThrow(step.SenderAlias, aliasMap);
        var recipient = GetHostOrThrow(step.RecipientAlias, aliasMap);

        using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stepCts.CancelAfter(TimeSpan.FromSeconds(10));

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = stepCts.Token.Register(() => tcs.TrySetCanceled(stepCts.Token));

        using var sub = _stateBus.Signals
            .Where(s => s.NodeId == recipient.Id && s.Category == SignalCategory.ChatTranscriptChanged)
            .Subscribe(_ => tcs.TrySetResult(true));

        var success = await _chatCoordinator.SendDirectMessageAsync(sender.Id, recipient.Id, step.Text, stepCts.Token);
        if (!success)
        {
            throw new ScenarioExecutionException($"SendDirectMessageAsync from '{step.SenderAlias}' to '{step.RecipientAlias}' failed.");
        }

        try
        {
            await tcs.Task;
        }
        catch (OperationCanceledException) when (stepCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out after 10s waiting for recipient '{step.RecipientAlias}' to receive chat message.");
        }
    }

    private static async Task ExecuteAssertDirectRatchetSessionAsync(
        AssertDirectRatchetSessionAssertion assertion,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        CancellationToken ct)
    {
        var h1 = GetHostOrThrow(assertion.Alias1, aliasMap);
        var h2 = GetHostOrThrow(assertion.Alias2, aliasMap);

        using var scope1 = h1.Services.CreateScope();
        var ratchetRepo1 = scope1.ServiceProvider.GetRequiredService<IRatchetSessionRepository>();
        var session1 = await ratchetRepo1.GetSessionAsync(h1.PublicIdentityId, h2.PublicIdentityId, DeviceId.Primary, ct);

        if (session1 == null)
        {
            throw new ScenarioAssertionException($"AssertDirectRatchetSession failed: Node '{assertion.Alias1}' does not have an active Double Ratchet session with '{assertion.Alias2}'.");
        }

        using var scope2 = h2.Services.CreateScope();
        var ratchetRepo2 = scope2.ServiceProvider.GetRequiredService<IRatchetSessionRepository>();
        var session2 = await ratchetRepo2.GetSessionAsync(h2.PublicIdentityId, h1.PublicIdentityId, DeviceId.Primary, ct);

        if (session2 == null)
        {
            throw new ScenarioAssertionException($"AssertDirectRatchetSession failed: Node '{assertion.Alias2}' does not have an active Double Ratchet session with '{assertion.Alias1}'.");
        }
    }

    private static async Task ExecuteAssertMessageDeliveredAsync(
        AssertMessageDeliveredAssertion assertion,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        CancellationToken ct)
    {
        var recipient = GetHostOrThrow(assertion.RecipientAlias, aliasMap);

        using var scope = recipient.Services.CreateScope();
        var chatRepo = scope.ServiceProvider.GetRequiredService<IChatMessageRepository>();
        var channelRepo = scope.ServiceProvider.GetRequiredService<IChannelRepository>();

        var found = false;
        foreach (var (_, peerHost) in aliasMap)
        {
            if (peerHost.Id == recipient.Id) continue;
            var directChannel = await channelRepo.GetDirectByPeerAsync(recipient.PublicIdentityId, peerHost.PublicIdentityId, ct);
            if (directChannel != null)
            {
                var messages = await chatRepo.GetByChannelIdAsync(directChannel.Id, ct);
                if (messages.Any(m => m.Content.Contains(assertion.ExpectedText, StringComparison.Ordinal)))
                {
                    found = true;
                    break;
                }
            }
        }

        if (!found)
        {
            throw new ScenarioAssertionException($"AssertMessageDelivered failed: Node '{assertion.RecipientAlias}' did not receive expected message text: \"{assertion.ExpectedText}\".");
        }
    }

    private static void ExecuteAssertZeroErrors(List<SimulatorLogEntry> errorLogs)
    {
        lock (errorLogs)
        {
            if (errorLogs.Count > 0)
            {
                var first = errorLogs[0];
                throw new ScenarioAssertionException($"AssertZeroErrors failed: {errorLogs.Count} error logs recorded during execution. First error: [{first.Category}] {first.Message}");
            }
        }
    }

    private static ISimulatedPersonaHost GetHostOrThrow(string alias, Dictionary<string, ISimulatedPersonaHost> aliasMap)
    {
        if (!aliasMap.TryGetValue(alias, out var host))
        {
            throw new ScenarioExecutionException($"Alias '{alias}' is not registered in the active scenario persona map. Available: [{string.Join(", ", aliasMap.Keys)}]");
        }
        return host;
    }
}
