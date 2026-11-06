using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Application2.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Simulator.Features.DirectChat;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.RelayedChat;
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
    private readonly RelayProvisioningCoordinator _relayCoordinator;
    private readonly ISimulatorLogSink _logSink;
    private readonly ISimulatedNodeFactory _nodeFactory;
    private readonly IFlightRecorderStateService? _flightRecorder;
    private readonly ISelectionStateService? _selectionState;

    public ScenarioRunnerService(
        ISimulatedWorldStateService worldState,
        ISimulatorStateBus stateBus,
        DirectHandshakeOrchestrator handshakeOrchestrator,
        DirectChatCoordinator chatCoordinator,
        RelayProvisioningCoordinator relayCoordinator,
        ISimulatorLogSink logSink,
        ISimulatedNodeFactory nodeFactory,
        IFlightRecorderStateService? flightRecorder = null,
        ISelectionStateService? selectionState = null)
    {
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _stateBus = stateBus ?? throw new ArgumentNullException(nameof(stateBus));
        _handshakeOrchestrator = handshakeOrchestrator ?? throw new ArgumentNullException(nameof(handshakeOrchestrator));
        _chatCoordinator = chatCoordinator ?? throw new ArgumentNullException(nameof(chatCoordinator));
        _relayCoordinator = relayCoordinator ?? throw new ArgumentNullException(nameof(relayCoordinator));
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
        _nodeFactory = nodeFactory ?? throw new ArgumentNullException(nameof(nodeFactory));
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
        var allSessionLogs = new List<SimulatorLogEntry>();
        using var logSub = _logSink.LogStream
            .Subscribe(entry =>
            {
                lock (allSessionLogs)
                {
                    allSessionLogs.Add(entry);
                    if (entry.Level >= LogLevel.Warning)
                    {
                        errorLogs.Add(entry);
                    }
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
        var relayAliasMap = new Dictionary<string, ISimulatedRelayHost>(StringComparer.OrdinalIgnoreCase);

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
                        await ExecuteSetListeningStateAsync(listeningStep, aliasMap, ct);
                        break;

                    case SendDirectMessageStep messageStep:
                        await ExecuteSendDirectMessageAsync(messageStep, aliasMap, ct);
                        break;

                    case SpawnRelayStep spawnRelayStep:
                        await ExecuteSpawnRelayAsync(spawnRelayStep, relayAliasMap, ct);
                        break;

                    case RegisterMailboxesStep regMailboxesStep:
                        await ExecuteRegisterMailboxesAsync(regMailboxesStep, aliasMap, relayAliasMap, ct);
                        break;

                    case PublishPreKeysStep pubKeysStep:
                        await ExecutePublishPreKeysAsync(pubKeysStep, aliasMap, relayAliasMap, ct);
                        break;

                    case InitiateRelayedHandshakeStep relayedHsStep:
                        await ExecuteInitiateRelayedHandshakeAsync(relayedHsStep, aliasMap, relayAliasMap, ct);
                        break;

                    case DuplicateRelayEnvelopeStep dupStep:
                        ExecuteDuplicateRelayEnvelope(dupStep, relayAliasMap);
                        break;

                    case FlipRelayEnvelopeBitStep flipStep:
                        ExecuteFlipRelayEnvelopeBit(flipStep, relayAliasMap);
                        break;

                    case ShuffleRelayQueueStep shuffleStep:
                        ExecuteShuffleRelayQueue(shuffleStep, relayAliasMap);
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

                    case AssertHostedOpkCountAssertion opkAssert:
                        await ExecuteAssertHostedOpkCountAsync(opkAssert, aliasMap, relayAliasMap, ct);
                        break;

                    case AssertMailboxQueueDepthAssertion queueAssert:
                        ExecuteAssertMailboxQueueDepth(queueAssert, relayAliasMap);
                        break;

                    case AssertLogContainsCodeAssertion logAssert:
                        ExecuteAssertLogContainsCode(logAssert, allSessionLogs);
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
            var host = await _nodeFactory.CreatePersonaHostAsync(alias, ct);

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

    private async Task ExecuteSpawnRelayAsync(
        SpawnRelayStep step,
        Dictionary<string, ISimulatedRelayHost> relayAliasMap,
        CancellationToken ct)
    {
        var host = await _nodeFactory.CreateRelayHostAsync(step.RelayAlias, ct);

        var summary = new SimulatedNodeSummary(
            host.Id,
            host.DisplayName,
            SimulatedNodeType.Relay,
            $"127.0.0.1:{host.BoundPort} (Relay)",
            IsPinned: true);

        _worldState.RegisterRelay(summary, host);
        _worldState.TogglePinCard(summary);
        relayAliasMap[step.RelayAlias] = host;
    }

    private async Task ExecuteRegisterMailboxesAsync(
        RegisterMailboxesStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        Dictionary<string, ISimulatedRelayHost> relayAliasMap,
        CancellationToken ct)
    {
        if (!relayAliasMap.TryGetValue(step.RelayAlias, out var relay))
        {
            throw new ScenarioExecutionException($"Relay '{step.RelayAlias}' not found in scenario.");
        }

        foreach (var alias in step.PersonaAliases)
        {
            if (!aliasMap.TryGetValue(alias, out var persona))
            {
                throw new ScenarioExecutionException($"Persona '{alias}' not found in scenario.");
            }

            var success = await _relayCoordinator.RegisterMailboxAsync(persona, relay, ct);
            if (!success)
            {
                throw new ScenarioExecutionException($"Failed to register mailbox for '{alias}' on relay '{step.RelayAlias}'.");
            }
        }
    }

    private async Task ExecutePublishPreKeysAsync(
        PublishPreKeysStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        Dictionary<string, ISimulatedRelayHost> relayAliasMap,
        CancellationToken ct)
    {
        if (!relayAliasMap.TryGetValue(step.RelayAlias, out var relay))
        {
            throw new ScenarioExecutionException($"Relay '{step.RelayAlias}' not found in scenario.");
        }

        foreach (var alias in step.PersonaAliases)
        {
            if (!aliasMap.TryGetValue(alias, out var persona))
            {
                throw new ScenarioExecutionException($"Persona '{alias}' not found in scenario.");
            }

            var success = await _relayCoordinator.PublishPreKeysAsync(persona, relay, step.OneTimeKeyCount, ct);
            if (!success)
            {
                throw new ScenarioExecutionException($"Failed to publish prekeys for '{alias}' to relay '{step.RelayAlias}'.");
            }
        }
    }

    private async Task ExecuteInitiateRelayedHandshakeAsync(
        InitiateRelayedHandshakeStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        Dictionary<string, ISimulatedRelayHost> relayAliasMap,
        CancellationToken ct)
    {
        if (!aliasMap.TryGetValue(step.InitiatorAlias, out var initiator))
            throw new ScenarioExecutionException($"Initiator '{step.InitiatorAlias}' not found.");
        if (!aliasMap.TryGetValue(step.TargetAlias, out var target))
            throw new ScenarioExecutionException($"Target '{step.TargetAlias}' not found.");
        if (!relayAliasMap.TryGetValue(step.RelayAlias, out var relay))
            throw new ScenarioExecutionException($"Relay '{step.RelayAlias}' not found.");

        var targetListening = target.IsListening.CurrentValue;

        Task signalTask = targetListening
            ? _stateBus.Signals
                .Where(s => s.NodeId == target.Id && s.Category == SignalCategory.PendingHandshakeChanged)
                .FirstAsync(cancellationToken: ct)
            : Task.CompletedTask;

        var success = await _relayCoordinator.InitiateRelayedHandshakeAsync(initiator.Id, target.Id, relay.Id, ct);
        if (!success)
        {
            throw new ScenarioExecutionException($"Relayed handshake failed: '{step.InitiatorAlias}' ➔ '{step.TargetAlias}'.");
        }

        if (targetListening)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            await signalTask.WaitAsync(linkedCts.Token);
        }
    }

    private static void ExecuteDuplicateRelayEnvelope(
        DuplicateRelayEnvelopeStep step,
        Dictionary<string, ISimulatedRelayHost> relayAliasMap)
    {
        if (!relayAliasMap.TryGetValue(step.RelayAlias, out var relay))
            throw new ScenarioExecutionException($"Relay '{step.RelayAlias}' not found.");

        var success = relay.ChaosController.DuplicateEnvelope(step.QueueId);
        if (!success)
        {
            throw new ScenarioExecutionException($"DuplicateEnvelope on '{step.RelayAlias}' failed (queue may be empty).");
        }
    }

    private static void ExecuteFlipRelayEnvelopeBit(
        FlipRelayEnvelopeBitStep step,
        Dictionary<string, ISimulatedRelayHost> relayAliasMap)
    {
        if (!relayAliasMap.TryGetValue(step.RelayAlias, out var relay))
            throw new ScenarioExecutionException($"Relay '{step.RelayAlias}' not found.");

        var success = relay.ChaosController.FlipBit(step.QueueId, step.ByteOffset);
        if (!success)
        {
            throw new ScenarioExecutionException($"FlipBit on '{step.RelayAlias}' failed (queue may be empty).");
        }
    }

    private static void ExecuteShuffleRelayQueue(
        ShuffleRelayQueueStep step,
        Dictionary<string, ISimulatedRelayHost> relayAliasMap)
    {
        if (!relayAliasMap.TryGetValue(step.RelayAlias, out var relay))
            throw new ScenarioExecutionException($"Relay '{step.RelayAlias}' not found.");

        var success = relay.ChaosController.ShuffleQueue();
        if (!success)
        {
            throw new ScenarioExecutionException($"ShuffleQueue on '{step.RelayAlias}' failed (need >= 2 envelopes in mailbox).");
        }
    }

    private async Task ExecuteInitiateDirectHandshakeAsync(
        InitiateDirectHandshakeStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        CancellationToken ct)
    {
        if (!aliasMap.TryGetValue(step.InitiatorAlias, out var initiator))
            throw new ScenarioExecutionException($"Initiator persona '{step.InitiatorAlias}' not found.");
        if (!aliasMap.TryGetValue(step.TargetAlias, out var target))
            throw new ScenarioExecutionException($"Target persona '{step.TargetAlias}' not found.");

        var targetListening = target.IsListening.CurrentValue;

        Task signalTask = targetListening
            ? _stateBus.Signals
                .Where(s => s.NodeId == target.Id && s.Category == SignalCategory.PendingHandshakeChanged)
                .FirstAsync(cancellationToken: ct)
            : Task.CompletedTask;

        var success = await _handshakeOrchestrator.InitiateDirectHandshakeAsync(initiator.Id, target.Id, ct);
        if (!success)
        {
            throw new ScenarioExecutionException($"InitiateDirectHandshakeAsync failed for '{step.InitiatorAlias}' -> '{step.TargetAlias}'.");
        }

        if (targetListening)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            await signalTask.WaitAsync(linkedCts.Token);
        }
    }

    private async Task ExecuteApprovePendingHandshakeAsync(
        ApprovePendingHandshakeStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        CancellationToken ct)
    {
        if (!aliasMap.TryGetValue(step.OwnerAlias, out var owner))
            throw new ScenarioExecutionException($"Owner persona '{step.OwnerAlias}' not found.");
        if (!aliasMap.TryGetValue(step.SenderAlias, out var sender))
            throw new ScenarioExecutionException($"Sender persona '{step.SenderAlias}' not found.");

        var signalTask = _stateBus.Signals
            .Where(s => s.NodeId == owner.Id && s.Category == SignalCategory.RatchetSessionChanged)
            .FirstAsync(cancellationToken: ct);

        var success = await _handshakeOrchestrator.AcceptHandshakeAsync(owner.Id, sender.PublicIdentityId, ct);
        if (!success)
        {
            throw new ScenarioExecutionException($"AcceptHandshakeAsync failed: '{step.OwnerAlias}' could not accept '{step.SenderAlias}'.");
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        await signalTask.WaitAsync(linkedCts.Token);
    }

    private async Task ExecuteRejectPendingHandshakeAsync(
        RejectPendingHandshakeStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        CancellationToken ct)
    {
        if (!aliasMap.TryGetValue(step.OwnerAlias, out var owner))
            throw new ScenarioExecutionException($"Owner persona '{step.OwnerAlias}' not found.");
        if (!aliasMap.TryGetValue(step.SenderAlias, out var sender))
            throw new ScenarioExecutionException($"Sender persona '{step.SenderAlias}' not found.");

        var success = await _handshakeOrchestrator.RejectHandshakeAsync(owner.Id, sender.PublicIdentityId, step.Block, ct);
        if (!success)
        {
            throw new ScenarioExecutionException($"RejectHandshakeAsync failed: '{step.OwnerAlias}' rejecting '{step.SenderAlias}'.");
        }
    }

    private static async Task ExecuteSetListeningStateAsync(
        SetListeningStateStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        CancellationToken ct)
    {
        if (!aliasMap.TryGetValue(step.Alias, out var host))
            throw new ScenarioExecutionException($"Persona '{step.Alias}' not found.");

        await host.SetListeningAsync(step.IsListening, ct);
    }

    private async Task ExecuteSendDirectMessageAsync(
        SendDirectMessageStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        CancellationToken ct)
    {
        if (!aliasMap.TryGetValue(step.SenderAlias, out var sender))
            throw new ScenarioExecutionException($"Sender persona '{step.SenderAlias}' not found.");
        if (!aliasMap.TryGetValue(step.RecipientAlias, out var recipient))
            throw new ScenarioExecutionException($"Recipient persona '{step.RecipientAlias}' not found.");

        Task signalTask = recipient.IsListening.CurrentValue
            ? _stateBus.Signals
                .Where(s => s.NodeId == recipient.Id && s.Category == SignalCategory.ChatTranscriptChanged)
                .FirstAsync(cancellationToken: ct)
            : Task.CompletedTask;

        var success = await _chatCoordinator.SendDirectMessageAsync(sender.Id, recipient.Id, step.Text, ct);
        if (!success)
        {
            throw new ScenarioExecutionException($"SendDirectMessageAsync failed: '{step.SenderAlias}' -> '{step.RecipientAlias}'.");
        }

        if (recipient.IsListening.CurrentValue)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            await signalTask.WaitAsync(linkedCts.Token);
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
        var ratchetQuery1 = scope1.ServiceProvider.GetRequiredService<ISimulatorRatchetQueryService>();
        var hasSession1 = await ratchetQuery1.HasActiveSessionAsync(h1.PublicIdentityId, h2.PublicIdentityId, DeviceId.Primary, ct);

        if (!hasSession1)
        {
            throw new ScenarioExecutionException($"AssertDirectRatchetSession failed: Node '{assertion.Alias1}' does not have an active Double Ratchet session with '{assertion.Alias2}'.");
        }

        using var scope2 = h2.Services.CreateScope();
        var ratchetQuery2 = scope2.ServiceProvider.GetRequiredService<ISimulatorRatchetQueryService>();
        var hasSession2 = await ratchetQuery2.HasActiveSessionAsync(h2.PublicIdentityId, h1.PublicIdentityId, DeviceId.Primary, ct);

        if (!hasSession2)
        {
            throw new ScenarioExecutionException($"AssertDirectRatchetSession failed: Node '{assertion.Alias2}' does not have an active Double Ratchet session with '{assertion.Alias1}'.");
        }
    }

    private static async Task ExecuteAssertMessageDeliveredAsync(
        AssertMessageDeliveredAssertion assertion,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        CancellationToken ct)
    {
        var recipient = GetHostOrThrow(assertion.RecipientAlias, aliasMap);

        using var scope = recipient.Services.CreateScope();
        var chatQuery = scope.ServiceProvider.GetRequiredService<ISimulatorChatQueryService>();
        var found = await chatQuery.HasMessageContainingAsync(assertion.ExpectedText, ct);

        if (!found)
        {
            throw new ScenarioExecutionException($"AssertMessageDelivered failed: Node '{assertion.RecipientAlias}' has not received expected message: \"{assertion.ExpectedText}\".");
        }
    }

    private static async Task ExecuteAssertHostedOpkCountAsync(
        AssertHostedOpkCountAssertion assertion,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        Dictionary<string, ISimulatedRelayHost> relayAliasMap,
        CancellationToken ct)
    {
        if (!relayAliasMap.TryGetValue(assertion.RelayAlias, out var relay))
            throw new ScenarioExecutionException($"Relay '{assertion.RelayAlias}' not found in scenario.");
        if (!aliasMap.TryGetValue(assertion.PersonaAlias, out var persona))
            throw new ScenarioExecutionException($"Persona '{assertion.PersonaAlias}' not found in scenario.");

        var actualCount = await relay.QueryService.GetHostedOpkCountAsync(persona.PublicIdentityId, DeviceId.Primary, ct);

        if (actualCount != assertion.ExpectedCount)
        {
            throw new ScenarioExecutionException($"AssertHostedOpkCount failed: expected {assertion.ExpectedCount} OPKs for '{assertion.PersonaAlias}' on '{assertion.RelayAlias}', but got {actualCount}.");
        }
    }

    private static void ExecuteAssertMailboxQueueDepth(
        AssertMailboxQueueDepthAssertion assertion,
        Dictionary<string, ISimulatedRelayHost> relayAliasMap)
    {
        if (!relayAliasMap.TryGetValue(assertion.RelayAlias, out var relay))
            throw new ScenarioExecutionException($"Relay '{assertion.RelayAlias}' not found in scenario.");

        var actual = relay.TotalHeldEnvelopeCount;
        if (actual != assertion.ExpectedDepth)
        {
            throw new ScenarioExecutionException($"AssertMailboxQueueDepth failed on '{assertion.RelayAlias}': expected depth {assertion.ExpectedDepth}, got {actual}.");
        }
    }

    private static void ExecuteAssertLogContainsCode(
        AssertLogContainsCodeAssertion assertion,
        List<SimulatorLogEntry> logs)
    {
        lock (logs)
        {
            var found = logs.Any(l =>
                (l.Message != null && l.Message.Contains(assertion.ExpectedCode, StringComparison.OrdinalIgnoreCase)) ||
                (l.Exception != null && l.Exception.Contains(assertion.ExpectedCode, StringComparison.OrdinalIgnoreCase)));

            if (!found)
            {
                throw new ScenarioExecutionException($"AssertLogContainsCode failed: expected log containing \"{assertion.ExpectedCode}\", but no matching log entry was found.");
            }
        }
    }

    private static void ExecuteAssertZeroErrors(List<SimulatorLogEntry> errorLogs)
    {
        lock (errorLogs)
        {
            if (errorLogs.Count > 0)
            {
                var summary = string.Join("; ", errorLogs.Take(3).Select(e => $"[{e.NodeName}] {e.Message}"));
                throw new ScenarioExecutionException($"AssertZeroErrors failed: {errorLogs.Count} error logs recorded. First: {summary}");
            }
        }
    }

    private static ISimulatedPersonaHost GetHostOrThrow(string alias, Dictionary<string, ISimulatedPersonaHost> map)
    {
        if (!map.TryGetValue(alias, out var host))
        {
            throw new ScenarioExecutionException($"Persona '{alias}' not found in scenario.");
        }
        return host;
    }
}
