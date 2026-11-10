using System.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Application.Ports;
using Percolator.Application2.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.PluginSdk;
using Percolator.Simulator.Features.DirectChat;
using Percolator.Simulator.Features.DiscoverySimulation;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.RelayedChat;
using Percolator.Simulator.Features.RichChatAndProfiles;
using Percolator.Simulator.Features.Scenarios.Model;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.SwarmSimulation;
using Percolator.Apps.Chat.Features.GroupInvitations;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Simulator.Features.GroupMessaging;
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
    private readonly IVirtualClockStateService _virtualClock;
    private readonly RichChatActionCoordinator _richChatCoordinator;
    private readonly ProfileSyncCoordinator _profileCoordinator;
    private readonly GroupChatCoordinator _groupCoordinator;
    private readonly DiscoverySimulationCoordinator _discoveryCoordinator;
    private readonly SwarmSimulationCoordinator _swarmCoordinator;
    private readonly IFlightRecorderStateService _flightRecorder;
    private readonly ISelectionStateService? _selectionState;
    private Guid? _lastSwarmSessionId;

    public ScenarioRunnerService(
        ISimulatedWorldStateService worldState,
        ISimulatorStateBus stateBus,
        DirectHandshakeOrchestrator handshakeOrchestrator,
        DirectChatCoordinator chatCoordinator,
        RelayProvisioningCoordinator relayCoordinator,
        ISimulatorLogSink logSink,
        ISimulatedNodeFactory nodeFactory,
        IVirtualClockStateService virtualClock,
        RichChatActionCoordinator richChatCoordinator,
        ProfileSyncCoordinator profileCoordinator,
        GroupChatCoordinator groupCoordinator,
        DiscoverySimulationCoordinator discoveryCoordinator,
        SwarmSimulationCoordinator swarmCoordinator,
        IFlightRecorderStateService flightRecorder,
        ISelectionStateService? selectionState = null)
    {
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _stateBus = stateBus ?? throw new ArgumentNullException(nameof(stateBus));
        _handshakeOrchestrator = handshakeOrchestrator ?? throw new ArgumentNullException(nameof(handshakeOrchestrator));
        _chatCoordinator = chatCoordinator ?? throw new ArgumentNullException(nameof(chatCoordinator));
        _relayCoordinator = relayCoordinator ?? throw new ArgumentNullException(nameof(relayCoordinator));
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
        _nodeFactory = nodeFactory ?? throw new ArgumentNullException(nameof(nodeFactory));
        _virtualClock = virtualClock ?? throw new ArgumentNullException(nameof(virtualClock));
        _richChatCoordinator = richChatCoordinator ?? throw new ArgumentNullException(nameof(richChatCoordinator));
        _profileCoordinator = profileCoordinator ?? throw new ArgumentNullException(nameof(profileCoordinator));
        _groupCoordinator = groupCoordinator ?? throw new ArgumentNullException(nameof(groupCoordinator));
        _discoveryCoordinator = discoveryCoordinator ?? throw new ArgumentNullException(nameof(discoveryCoordinator));
        _swarmCoordinator = swarmCoordinator ?? throw new ArgumentNullException(nameof(swarmCoordinator));
        _flightRecorder = flightRecorder ?? throw new ArgumentNullException(nameof(flightRecorder));
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

        _flightRecorder.SetReplayStatus($"Replaying: '{scenario.Name}'...");

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
        var groupAliasMap = new Dictionary<string, ChannelId>(StringComparer.OrdinalIgnoreCase);
        _lastSwarmSessionId = null;

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

                _flightRecorder.SetReplayStatus($"Step {stepNum}/{scenario.Steps.Count}: {step.DisplayText}");

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

                    case AdvanceVirtualTimeStep advanceStep:
                        await ExecuteAdvanceVirtualTimeAsync(advanceStep, ct);
                        break;

                    case SendDisappearingMessageStep disappearingStep:
                        await ExecuteSendDisappearingMessageAsync(disappearingStep, aliasMap, ct);
                        break;

                    case AddReactionStep reactionStep:
                        await ExecuteAddReactionAsync(reactionStep, aliasMap, ct);
                        break;

                    case EditMessageStep editStep:
                        await ExecuteEditMessageAsync(editStep, aliasMap, ct);
                        break;

                    case DeleteMessageStep deleteStep:
                        await ExecuteDeleteMessageAsync(deleteStep, aliasMap, ct);
                        break;

                    case MarkConversationReadStep readStep:
                        await ExecuteMarkConversationReadAsync(readStep, aliasMap, ct);
                        break;

                    case RerollProfileStep rerollStep:
                        await ExecuteRerollProfileAsync(rerollStep, aliasMap, ct);
                        break;
                    case CreateGroupStep createGroup:
                        await ExecuteCreateGroupAsync(createGroup, aliasMap, relayAliasMap, groupAliasMap, ct);
                        break;

                    case AcceptGroupInviteStep acceptInvite:
                        await ExecuteAcceptGroupInviteAsync(acceptInvite, aliasMap, groupAliasMap, ct);
                        break;

                    case DeclineGroupInviteStep declineInvite:
                        await ExecuteDeclineGroupInviteAsync(declineInvite, aliasMap, groupAliasMap, ct);
                        break;

                    case SendGroupMessageStep sendGroupMsg:
                        await ExecuteSendGroupMessageAsync(sendGroupMsg, aliasMap, groupAliasMap, ct);
                        break;

                    case FloodGroupMessagesStep floodStep:
                        await ExecuteFloodGroupMessagesAsync(floodStep, aliasMap, groupAliasMap, ct);
                        break;

                    case AddGroupMemberStep addMember:
                        await ExecuteAddGroupMemberAsync(addMember, aliasMap, relayAliasMap, groupAliasMap, ct);
                        break;

                    case EvictGroupMemberStep evictMember:
                        await ExecuteEvictGroupMemberAsync(evictMember, aliasMap, relayAliasMap, groupAliasMap, ct);
                        break;

                    case RenameGroupStep renameGroup:
                        await ExecuteRenameGroupAsync(renameGroup, aliasMap, relayAliasMap, groupAliasMap, ct);
                        break;

                    case SetDiscoveryVisibilityStep setVisibilityStep:
                        await ExecuteSetDiscoveryVisibilityAsync(setVisibilityStep, aliasMap, ct);
                        break;

                    case PublishBlindedLocatorStep publishLocatorStep:
                        await ExecutePublishBlindedLocatorAsync(publishLocatorStep, aliasMap, relayAliasMap, ct);
                        break;

                    case ResolveBlindedLocatorStep resolveLocatorStep:
                        await ExecuteResolveBlindedLocatorAsync(resolveLocatorStep, aliasMap, relayAliasMap, ct);
                        break;

                    case QueryNearbyPeersStep queryNearbyStep:
                        await ExecuteQueryNearbyPeersAsync(queryNearbyStep, aliasMap, relayAliasMap, ct);
                        break;

                    case ConnectDiscoveredPeerStep connectDiscoveredStep:
                        await ExecuteConnectDiscoveredPeerAsync(connectDiscoveredStep, aliasMap, relayAliasMap, ct);
                        break;

                    case InitiateFileTransferStep initTransferStep:
                        await ExecuteInitiateFileTransferAsync(initTransferStep, aliasMap, ct);
                        break;

                    case AcceptFileTransferStep acceptTransferStep:
                        await ExecuteAcceptFileTransferAsync(acceptTransferStep, aliasMap, ct);
                        break;

                    case InjectLateDownloaderStep lateJoinerStep:
                        await ExecuteInjectLateDownloaderAsync(lateJoinerStep, aliasMap, ct);
                        break;

                    case PauseFileTransferStep pauseStep:
                        await ExecutePauseFileTransferAsync(pauseStep, ct);
                        break;

                    case ResumeFileTransferStep resumeStep:
                        await ExecuteResumeFileTransferAsync(resumeStep, ct);
                        break;

                    case CancelFileTransferStep cancelStep:
                        await ExecuteCancelFileTransferAsync(cancelStep, ct);
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

                _flightRecorder.SetReplayStatus($"Verifying {assertNum}/{scenario.Assertions.Count}: {assertion.DisplayText}");

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
                        await ExecuteAssertLogContainsCodeAsync(logAssert, allSessionLogs, ct);
                        break;

                    case AssertMessageExpiredAssertion expiredAssert:
                        await ExecuteAssertMessageExpiredAsync(expiredAssert, aliasMap, ct);
                        break;

                    case AssertReactionPresentAssertion reactionAssert:
                        await ExecuteAssertReactionPresentAsync(reactionAssert, aliasMap, ct);
                        break;

                    case AssertMessageEditedAssertion editedAssert:
                        await ExecuteAssertMessageEditedAsync(editedAssert, aliasMap, ct);
                        break;

                    case AssertMessageTombstonedAssertion tombstoneAssert:
                        await ExecuteAssertMessageTombstonedAsync(tombstoneAssert, aliasMap, ct);
                        break;

                    case AssertMessageReadAssertion readAssert:
                        await ExecuteAssertMessageReadAsync(readAssert, aliasMap, ct);
                        break;

                    case AssertProfileSyncedAssertion profileAssert:
                        await ExecuteAssertProfileSyncedAsync(profileAssert, aliasMap, ct);
                        break;
                    case AssertGroupEpochAssertion epochAssert:
                        await ExecuteAssertGroupEpochAsync(epochAssert, groupAliasMap, ct);
                        break;

                    case AssertGroupMessageDeliveredAssertion groupMsgAssert:
                        await ExecuteAssertGroupMessageDeliveredAsync(groupMsgAssert, aliasMap, groupAliasMap, ct);
                        break;

                    case AssertMemberCannotDecryptAssertion forwardSecrecyAssert:
                        await ExecuteAssertMemberCannotDecryptAsync(forwardSecrecyAssert, aliasMap, groupAliasMap, ct);
                        break;

                    case AssertBlindedLocatorResolvedAssertion rendezvousAssert:
                        ExecuteAssertBlindedLocatorResolved(rendezvousAssert, aliasMap);
                        break;

                    case AssertNearbyCandidateCountAssertion nearbyAssert:
                        ExecuteAssertNearbyCandidateCount(nearbyAssert, aliasMap);
                        break;

                    case AssertFileTransferProgressAssertion progressAssert:
                        await ExecuteAssertFileTransferProgressAsync(progressAssert, aliasMap, ct);
                        break;

                    case AssertFileTransferCompletedAssertion completedAssert:
                        await ExecuteAssertFileTransferCompletedAsync(completedAssert, aliasMap, ct);
                        break;

                    case AssertFileIntegrityVerifiedAssertion integrityAssert:
                        ExecuteAssertFileIntegrityVerified(integrityAssert, aliasMap);
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

            _flightRecorder.SetReplayStatus($"PASS: '{scenario.Name}'");
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

            _flightRecorder.SetReplayStatus($"FAILED: {ex.Message}");
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

    private async Task ExecuteWithTranscriptSignalWaitAsync(
        ISimulatedPersonaHost recipient,
        Func<Task<bool>> action,
        string failureMessage,
        CancellationToken ct)
    {
        Task signalTask = recipient.IsListening.CurrentValue
            ? _stateBus.Signals
                .Where(s => s.NodeId == recipient.Id && s.Category == SignalCategory.ChatTranscriptChanged)
                .FirstAsync(cancellationToken: ct)
            : Task.CompletedTask;

        var success = await action();
        if (!success)
        {
            throw new ScenarioExecutionException(failureMessage);
        }

        if (recipient.IsListening.CurrentValue)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            await signalTask.WaitAsync(linkedCts.Token);
        }
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

        await ExecuteWithTranscriptSignalWaitAsync(
            recipient,
            () => _chatCoordinator.SendDirectMessageAsync(sender.Id, recipient.Id, step.Text, expireAfterSeconds: null, ct: ct),
            $"SendDirectMessageAsync failed: '{step.SenderAlias}' -> '{step.RecipientAlias}'.",
            ct);
    }

    private async Task ExecuteAdvanceVirtualTimeAsync(AdvanceVirtualTimeStep step, CancellationToken ct)
    {
        await _virtualClock.AdvanceTimeAsync(TimeSpan.FromSeconds(step.Seconds), ct);
    }

    private async Task ExecuteSendDisappearingMessageAsync(
        SendDisappearingMessageStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        CancellationToken ct)
    {
        if (!aliasMap.TryGetValue(step.SenderAlias, out var sender))
            throw new ScenarioExecutionException($"Sender persona '{step.SenderAlias}' not found.");
        if (!aliasMap.TryGetValue(step.RecipientAlias, out var recipient))
            throw new ScenarioExecutionException($"Recipient persona '{step.RecipientAlias}' not found.");

        await ExecuteWithTranscriptSignalWaitAsync(
            recipient,
            () => _chatCoordinator.SendDirectMessageAsync(sender.Id, recipient.Id, step.Text, step.ExpireAfterSeconds, ct),
            $"SendDisappearingMessageAsync failed: '{step.SenderAlias}' -> '{step.RecipientAlias}'.",
            ct);
    }

    private async Task ExecuteAddReactionAsync(
        AddReactionStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        CancellationToken ct)
    {
        if (_richChatCoordinator == null)
            throw new ScenarioExecutionException("RichChatActionCoordinator is not configured for scenario replay.");

        if (!aliasMap.TryGetValue(step.SenderAlias, out var sender))
            throw new ScenarioExecutionException($"Sender persona '{step.SenderAlias}' not found.");
        if (!aliasMap.TryGetValue(step.RecipientAlias, out var recipient))
            throw new ScenarioExecutionException($"Recipient persona '{step.RecipientAlias}' not found.");

        SimulatorChatMessageReadModel? targetMessage;
        using (var scope = sender.Services.CreateScope())
        {
            var chatQuery = scope.ServiceProvider.GetRequiredService<ISimulatorChatQueryService>();
            targetMessage = await chatQuery.FindMessageAsync(step.TargetMessageText, ct);
        }

        if (targetMessage == null)
        {
            using var scope = recipient.Services.CreateScope();
            var chatQuery = scope.ServiceProvider.GetRequiredService<ISimulatorChatQueryService>();
            targetMessage = await chatQuery.FindMessageAsync(step.TargetMessageText, ct);
        }

        if (targetMessage == null)
        {
            throw new ScenarioExecutionException($"Cannot react: Message containing \"{step.TargetMessageText}\" was not found in conversation between '{step.SenderAlias}' and '{step.RecipientAlias}'.");
        }

        await ExecuteWithTranscriptSignalWaitAsync(
            recipient,
            () => _richChatCoordinator.AddReactionAsync(sender.Id, recipient.Id, targetMessage.MessageId, step.Emoji, ct),
            $"AddReactionAsync failed: '{step.SenderAlias}' -> '{step.RecipientAlias}'.",
            ct);
    }

    private async Task ExecuteEditMessageAsync(
        EditMessageStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        CancellationToken ct)
    {
        if (_richChatCoordinator == null)
            throw new ScenarioExecutionException("RichChatActionCoordinator is not configured for scenario replay.");

        if (!aliasMap.TryGetValue(step.SenderAlias, out var sender))
            throw new ScenarioExecutionException($"Sender persona '{step.SenderAlias}' not found.");
        if (!aliasMap.TryGetValue(step.RecipientAlias, out var recipient))
            throw new ScenarioExecutionException($"Recipient persona '{step.RecipientAlias}' not found.");

        SimulatorChatMessageReadModel? targetMessage;
        using (var scope = sender.Services.CreateScope())
        {
            var chatQuery = scope.ServiceProvider.GetRequiredService<ISimulatorChatQueryService>();
            targetMessage = await chatQuery.FindMessageAsync(step.TargetMessageText, ct);
        }

        if (targetMessage == null)
        {
            throw new ScenarioExecutionException($"Cannot edit: Message containing \"{step.TargetMessageText}\" was not found on author '{step.SenderAlias}'.");
        }

        await ExecuteWithTranscriptSignalWaitAsync(
            recipient,
            () => _richChatCoordinator.EditMessageAsync(sender.Id, recipient.Id, targetMessage.MessageId, step.NewText, ct),
            $"EditMessageAsync failed: '{step.SenderAlias}' -> '{step.RecipientAlias}'.",
            ct);
    }

    private async Task ExecuteDeleteMessageAsync(
        DeleteMessageStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        CancellationToken ct)
    {
        if (_richChatCoordinator == null)
            throw new ScenarioExecutionException("RichChatActionCoordinator is not configured for scenario replay.");

        if (!aliasMap.TryGetValue(step.SenderAlias, out var sender))
            throw new ScenarioExecutionException($"Sender persona '{step.SenderAlias}' not found.");
        if (!aliasMap.TryGetValue(step.RecipientAlias, out var recipient))
            throw new ScenarioExecutionException($"Recipient persona '{step.RecipientAlias}' not found.");

        SimulatorChatMessageReadModel? targetMessage;
        using (var scope = sender.Services.CreateScope())
        {
            var chatQuery = scope.ServiceProvider.GetRequiredService<ISimulatorChatQueryService>();
            targetMessage = await chatQuery.FindMessageAsync(step.TargetMessageText, ct);
        }

        if (targetMessage == null)
        {
            throw new ScenarioExecutionException($"Cannot delete: Message containing \"{step.TargetMessageText}\" was not found on author '{step.SenderAlias}'.");
        }

        await ExecuteWithTranscriptSignalWaitAsync(
            recipient,
            () => _richChatCoordinator.DeleteMessageAsync(sender.Id, recipient.Id, targetMessage.MessageId, ct),
            $"DeleteMessageAsync failed: '{step.SenderAlias}' -> '{step.RecipientAlias}'.",
            ct);
    }

    private async Task ExecuteMarkConversationReadAsync(
        MarkConversationReadStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        CancellationToken ct)
    {
        if (!aliasMap.TryGetValue(step.OwnerAlias, out var owner))
            throw new ScenarioExecutionException($"Owner persona '{step.OwnerAlias}' not found.");
        if (!aliasMap.TryGetValue(step.PeerAlias, out var peer))
            throw new ScenarioExecutionException($"Peer persona '{step.PeerAlias}' not found.");

        using var scope = owner.Services.CreateScope();
        var chatQuery = scope.ServiceProvider.GetRequiredService<ISimulatorChatQueryService>();
        var channelId = ChannelIdUtility.DeterministicFor(owner.PublicIdentityId, peer.PublicIdentityId);
        var transcript = await chatQuery.GetTranscriptAsync(channelId, ct);
        var lastIncoming = transcript.LastOrDefault(m => m.AuthorId == peer.PublicIdentityId);
        if (lastIncoming == null)
        {
            throw new ScenarioExecutionException($"MarkConversationRead: No incoming messages from '{step.PeerAlias}' found on '{step.OwnerAlias}'.");
        }

        await ExecuteWithTranscriptSignalWaitAsync(
            peer,
            () => _chatCoordinator.MarkConversationAsReadAsync(owner.Id, peer.Id, lastIncoming.MessageId, ct),
            $"MarkConversationAsReadAsync failed for '{step.OwnerAlias}'.",
            ct);
    }

    private async Task ExecuteRerollProfileAsync(
        RerollProfileStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        CancellationToken ct)
    {
        var persona = GetHostOrThrow(step.PersonaAlias, aliasMap);
        var success = await _profileCoordinator.RerollProfileAsync(persona.Id, step.NewDisplayName, ct: ct);
        if (!success)
        {
            throw new ScenarioExecutionException($"RerollProfileStep failed for '{step.PersonaAlias}'.");
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
            throw new ScenarioExecutionException($"Relay '{assertion.RelayAlias}' not found.");

        var actual = relay.TotalHeldEnvelopeCount;
        if (actual != assertion.ExpectedDepth)
        {
            throw new ScenarioExecutionException($"AssertMailboxQueueDepth failed on '{assertion.RelayAlias}': expected depth {assertion.ExpectedDepth}, got {actual}.");
        }
    }

    private static async Task ExecuteAssertLogContainsCodeAsync(
        AssertLogContainsCodeAssertion assertion,
        List<SimulatorLogEntry> logs,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            lock (logs)
            {
                var found = logs.Any(l =>
                    (l.Message != null && l.Message.Contains(assertion.ExpectedCode, StringComparison.OrdinalIgnoreCase)) ||
                    (l.Exception != null && l.Exception.Contains(assertion.ExpectedCode, StringComparison.OrdinalIgnoreCase)));

                if (found) return;
            }

            await Task.Delay(50, ct);
        }

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

    private static async Task ExecuteAssertMessageExpiredAsync(
        AssertMessageExpiredAssertion assertion,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        CancellationToken ct)
    {
        var persona = GetHostOrThrow(assertion.PersonaAlias, aliasMap);
        using var scope = persona.Services.CreateScope();
        var chatQuery = scope.ServiceProvider.GetRequiredService<ISimulatorChatQueryService>();
        var found = await chatQuery.HasMessageContainingAsync(assertion.ExpectedText, ct);
        if (found)
        {
            throw new ScenarioExecutionException($"AssertMessageExpired failed: Message \"{assertion.ExpectedText}\" still exists on '{assertion.PersonaAlias}'.");
        }
    }

    private static async Task ExecuteAssertReactionPresentAsync(
        AssertReactionPresentAssertion assertion,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        CancellationToken ct)
    {
        var persona = GetHostOrThrow(assertion.PersonaAlias, aliasMap);
        using var scope = persona.Services.CreateScope();
        var chatQuery = scope.ServiceProvider.GetRequiredService<ISimulatorChatQueryService>();
        var hasReaction = await chatQuery.HasReactionAsync(assertion.MessageText, assertion.ExpectedEmoji, ct);
        if (!hasReaction)
        {
            throw new ScenarioExecutionException($"AssertReactionPresent failed: Reaction '{assertion.ExpectedEmoji}' on message \"{assertion.MessageText}\" was not found on '{assertion.PersonaAlias}'.");
        }
    }

    private static async Task ExecuteAssertMessageEditedAsync(
        AssertMessageEditedAssertion assertion,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        CancellationToken ct)
    {
        var persona = GetHostOrThrow(assertion.PersonaAlias, aliasMap);
        using var scope = persona.Services.CreateScope();
        var chatQuery = scope.ServiceProvider.GetRequiredService<ISimulatorChatQueryService>();
        var isEdited = await chatQuery.IsMessageEditedAsync(assertion.ExpectedNewText, ct);
        if (!isEdited)
        {
            throw new ScenarioExecutionException($"AssertMessageEdited failed: Message with text \"{assertion.ExpectedNewText}\" and edit timestamp not found on '{assertion.PersonaAlias}'.");
        }
    }

    private static async Task ExecuteAssertMessageTombstonedAsync(
        AssertMessageTombstonedAssertion assertion,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        CancellationToken ct)
    {
        var persona = GetHostOrThrow(assertion.PersonaAlias, aliasMap);
        using var scope = persona.Services.CreateScope();
        var chatQuery = scope.ServiceProvider.GetRequiredService<ISimulatorChatQueryService>();
        var stillActive = await chatQuery.HasMessageContainingAsync(assertion.OriginalMessageText, ct);
        if (stillActive)
        {
            throw new ScenarioExecutionException($"AssertMessageTombstoned failed: Message \"{assertion.OriginalMessageText}\" is still active (not deleted) on '{assertion.PersonaAlias}'.");
        }

        var hasTombstone = await chatQuery.HasAnyTombstoneAsync(ct);
        if (!hasTombstone)
        {
            throw new ScenarioExecutionException($"AssertMessageTombstoned failed: No tombstoned message found in database on '{assertion.PersonaAlias}'.");
        }
    }

    private static async Task ExecuteAssertMessageReadAsync(
        AssertMessageReadAssertion assertion,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        CancellationToken ct)
    {
        var persona = GetHostOrThrow(assertion.PersonaAlias, aliasMap);
        using var scope = persona.Services.CreateScope();
        var chatQuery = scope.ServiceProvider.GetRequiredService<ISimulatorChatQueryService>();
        var isRead = await chatQuery.IsMessageReadAsync(assertion.MessageText, ct);
        if (!isRead)
        {
            throw new ScenarioExecutionException($"AssertMessageRead failed: Message containing \"{assertion.MessageText}\" has not been marked as read on '{assertion.PersonaAlias}'.");
        }
    }

    private static async Task ExecuteAssertProfileSyncedAsync(
        AssertProfileSyncedAssertion assertion,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        CancellationToken ct)
    {
        var observer = GetHostOrThrow(assertion.ObserverAlias, aliasMap);
        var target = GetHostOrThrow(assertion.TargetAlias, aliasMap);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        PeerProfileRecord? profileRecord = null;

        while (DateTime.UtcNow < deadline)
        {
            using (var scope = observer.Services.CreateScope())
            {
                var profileRepo = scope.ServiceProvider.GetRequiredService<IPeerProfileRepository>();
                profileRecord = await profileRepo.GetByPeerIdAsync(target.PublicIdentityId, ct);
                if (profileRecord != null && profileRecord.DisplayName == assertion.ExpectedDisplayName)
                {
                    break;
                }
            }

            await Task.Delay(100, ct);
        }

        if (profileRecord == null)
        {
            throw new ScenarioExecutionException(
                $"AssertProfileSynced failed: Observer '{assertion.ObserverAlias}' has no cached profile record for target '{assertion.TargetAlias}'.");
        }

        if (profileRecord.DisplayName != assertion.ExpectedDisplayName)
        {
            throw new ScenarioExecutionException(
                $"AssertProfileSynced failed: Observer '{assertion.ObserverAlias}' profile DisplayName is '{profileRecord.DisplayName}', expected '{assertion.ExpectedDisplayName}'.");
        }

        if (profileRecord.AvatarReference == null)
        {
            throw new ScenarioExecutionException(
                $"AssertProfileSynced failed: Observer '{assertion.ObserverAlias}' profile has null AvatarReference for '{assertion.TargetAlias}'.");
        }

        using (var scope = observer.Services.CreateScope())
        {
            var contactRepo = scope.ServiceProvider.GetService<IPeerContactRepository>();
            if (contactRepo != null)
            {
                var contact = await contactRepo.GetByPeerIdAsync(observer.PublicIdentityId, target.PublicIdentityId, ct);
                if (contact != null)
                {
                    if (contact.Nickname.Value == assertion.ExpectedDisplayName && assertion.TargetAlias != assertion.ExpectedDisplayName)
                    {
                        throw new ScenarioExecutionException(
                            $"AssertProfileSynced failed: Local contact Nickname on '{assertion.ObserverAlias}' was mutated to '{contact.Nickname.Value}'. Contact nicknames must remain immutable against remote profile syncs.");
                    }
                }
            }

            var chunkStore = scope.ServiceProvider.GetService<ILocalChunkStore>();
            if (chunkStore != null && profileRecord.AvatarReference != null)
            {
                var avatarDeadline = DateTime.UtcNow.AddSeconds(5);
                var hasAvatar = false;
                while (DateTime.UtcNow < avatarDeadline)
                {
                    hasAvatar = await chunkStore.HasBlobAsync(profileRecord.AvatarReference.Id, ct);
                    if (hasAvatar) break;
                    await Task.Delay(100, ct);
                }

                if (!hasAvatar)
                {
                    throw new ScenarioExecutionException(
                        $"AssertProfileSynced failed: Avatar blob '{profileRecord.AvatarReference.Id}' was not downloaded to local chunk store on '{assertion.ObserverAlias}'.");
                }
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

    private async Task ExecuteCreateGroupAsync(
        CreateGroupStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        Dictionary<string, ISimulatedRelayHost> relayAliasMap,
        Dictionary<string, ChannelId> groupAliasMap,
        CancellationToken ct)
    {
        if (!aliasMap.TryGetValue(step.AdminAlias, out var admin))
            throw new ScenarioExecutionException($"Persona '{step.AdminAlias}' not found.");
        if (!relayAliasMap.TryGetValue(step.RelayAlias, out var relay))
            throw new ScenarioExecutionException($"Relay '{step.RelayAlias}' not found.");

        var members = new List<ISimulatedPersonaHost>();
        foreach (var memberAlias in step.MemberAliases)
        {
            if (aliasMap.TryGetValue(memberAlias, out var m))
            {
                members.Add(m);
            }
            else
            {
                throw new ScenarioExecutionException($"Member persona '{memberAlias}' not found.");
            }
        }

        var channelId = await _groupCoordinator.CreateGroupOnRelayAsync(admin, relay, step.GroupName, members, ct);
        groupAliasMap[step.GroupName] = channelId;
    }

    private async Task ExecuteAcceptGroupInviteAsync(
        AcceptGroupInviteStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        Dictionary<string, ChannelId> groupAliasMap,
        CancellationToken ct)
    {
        if (!aliasMap.TryGetValue(step.PersonaAlias, out var persona))
            throw new ScenarioExecutionException($"Persona '{step.PersonaAlias}' not found.");
        if (!groupAliasMap.TryGetValue(step.GroupName, out var channelId))
            throw new ScenarioExecutionException($"Group '{step.GroupName}' not found in scenario.");

        using var scope = persona.Services.CreateScope();
        var inviteRepo = scope.ServiceProvider.GetRequiredService<IGroupInvitationRepository>();
        var queryService = scope.ServiceProvider.GetRequiredService<IGroupInvitationQueryService>();

        PendingGroupInvitation? invitation = null;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var summaries = await queryService.GetPendingInvitationsAsync(ct);
            var sum = summaries.FirstOrDefault(s => s.TargetGroupChannelId == channelId);
            if (sum != null)
            {
                invitation = await inviteRepo.GetByIdAsync(sum.InvitationId, ct);
                if (invitation != null) break;
            }

            using var loopCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, loopCts.Token);
            try
            {
                await _stateBus.Signals.Where(s => s.NodeId == persona.Id).FirstAsync(cancellationToken: linked.Token);
            }
            catch (OperationCanceledException) when (loopCts.IsCancellationRequested) { }
        }

        if (invitation == null)
            throw new ScenarioExecutionException($"No pending invitation found for '{step.PersonaAlias}' in '{step.GroupName}'.");

        var success = await _groupCoordinator.AcceptGroupInviteAsync(persona, invitation.InvitationId, ct);
        if (!success)
            throw new ScenarioExecutionException($"Failed to accept group invitation for '{step.PersonaAlias}'.");
    }

    private async Task ExecuteDeclineGroupInviteAsync(
        DeclineGroupInviteStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        Dictionary<string, ChannelId> groupAliasMap,
        CancellationToken ct)
    {
        if (!aliasMap.TryGetValue(step.PersonaAlias, out var persona))
            throw new ScenarioExecutionException($"Persona '{step.PersonaAlias}' not found.");
        if (!groupAliasMap.TryGetValue(step.GroupName, out var channelId))
            throw new ScenarioExecutionException($"Group '{step.GroupName}' not found in scenario.");

        using var scope = persona.Services.CreateScope();
        var queryService = scope.ServiceProvider.GetRequiredService<IGroupInvitationQueryService>();
        var summaries = await queryService.GetPendingInvitationsAsync(ct);
        var sum = summaries.FirstOrDefault(s => s.TargetGroupChannelId == channelId);
        if (sum == null)
            throw new ScenarioExecutionException($"No pending invitation found for '{step.PersonaAlias}' in '{step.GroupName}'.");

        var success = await _groupCoordinator.DeclineGroupInviteAsync(persona, sum.InvitationId, ct);
        if (!success)
            throw new ScenarioExecutionException($"Failed to decline group invitation for '{step.PersonaAlias}'.");
    }

    private async Task ExecuteSendGroupMessageAsync(
        SendGroupMessageStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        Dictionary<string, ChannelId> groupAliasMap,
        CancellationToken ct)
    {
        if (!aliasMap.TryGetValue(step.SenderAlias, out var sender))
            throw new ScenarioExecutionException($"Persona '{step.SenderAlias}' not found.");
        if (!groupAliasMap.TryGetValue(step.GroupName, out var channelId))
            throw new ScenarioExecutionException($"Group '{step.GroupName}' not found in scenario.");

        using var senderScope = sender.Services.CreateScope();
        var channelRepo = senderScope.ServiceProvider.GetRequiredService<IChannelRepository>();
        var groupChannel = await channelRepo.GetGroupByIdAsync(channelId, ct);
        var listeningRecipients = new List<ISimulatedPersonaHost>();
        if (groupChannel != null)
        {
            foreach (var member in groupChannel.Members)
            {
                if (member.Id != sender.PublicIdentityId)
                {
                    var host = _worldState.FindPersonaHostByIdentity(member.Id);
                    if (host != null && host.IsListening.CurrentValue)
                    {
                        listeningRecipients.Add(host);
                    }
                }
            }
        }

        var signalTasks = listeningRecipients
            .Select(h => _stateBus.Signals
                .Where(s => s.NodeId == h.Id && s.Category == SignalCategory.ChatTranscriptChanged && (s.TargetChannelId == null || s.TargetChannelId == channelId))
                .FirstAsync(cancellationToken: ct))
            .ToList();

        var success = await _groupCoordinator.SendGroupMessageAsync(sender, channelId, step.Text, expireAfterSeconds: null, ct);
        if (!success)
            throw new ScenarioExecutionException($"Failed to send group message from '{step.SenderAlias}'.");

        if (signalTasks.Count > 0)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            await Task.WhenAll(signalTasks).WaitAsync(linkedCts.Token);
        }
    }

    private async Task ExecuteFloodGroupMessagesAsync(
        FloodGroupMessagesStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        Dictionary<string, ChannelId> groupAliasMap,
        CancellationToken ct)
    {
        if (!aliasMap.TryGetValue(step.SenderAlias, out var sender))
            throw new ScenarioExecutionException($"Persona '{step.SenderAlias}' not found.");
        if (!groupAliasMap.TryGetValue(step.GroupName, out var channelId))
            throw new ScenarioExecutionException($"Group '{step.GroupName}' not found in scenario.");

        var count = await _groupCoordinator.FloodGroupMessagesAsync(sender, channelId, step.Count, ct);
        if (count == 0)
            throw new ScenarioExecutionException($"Failed to flood {step.Count} group messages from '{step.SenderAlias}'.");
    }

    private async Task ExecuteAddGroupMemberAsync(
        AddGroupMemberStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        Dictionary<string, ISimulatedRelayHost> relayAliasMap,
        Dictionary<string, ChannelId> groupAliasMap,
        CancellationToken ct)
    {
        if (!aliasMap.TryGetValue(step.AdminAlias, out var admin))
            throw new ScenarioExecutionException($"Admin persona '{step.AdminAlias}' not found.");
        if (!aliasMap.TryGetValue(step.NewMemberAlias, out var newMember))
            throw new ScenarioExecutionException($"New member persona '{step.NewMemberAlias}' not found.");
        if (!groupAliasMap.TryGetValue(step.GroupName, out var channelId))
            throw new ScenarioExecutionException($"Group '{step.GroupName}' not found in scenario.");

        var relay = admin.HomeRelay ?? _worldState.GetAllRelayHosts().FirstOrDefault()
            ?? throw new ScenarioExecutionException("No relay host available for group mutation.");

        var success = await _groupCoordinator.AddGroupMemberAsync(admin, relay, channelId, newMember, ct);
        if (!success)
            throw new ScenarioExecutionException($"Failed to add '{step.NewMemberAlias}' to group '{step.GroupName}'.");
    }

    private async Task ExecuteEvictGroupMemberAsync(
        EvictGroupMemberStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        Dictionary<string, ISimulatedRelayHost> relayAliasMap,
        Dictionary<string, ChannelId> groupAliasMap,
        CancellationToken ct)
    {
        if (!aliasMap.TryGetValue(step.AdminAlias, out var admin))
            throw new ScenarioExecutionException($"Admin persona '{step.AdminAlias}' not found.");
        if (!aliasMap.TryGetValue(step.EvictedMemberAlias, out var member))
            throw new ScenarioExecutionException($"Evicted persona '{step.EvictedMemberAlias}' not found.");
        if (!groupAliasMap.TryGetValue(step.GroupName, out var channelId))
            throw new ScenarioExecutionException($"Group '{step.GroupName}' not found in scenario.");

        var relay = admin.HomeRelay ?? _worldState.GetAllRelayHosts().FirstOrDefault()
            ?? throw new ScenarioExecutionException("No relay host available for group mutation.");

        var success = await _groupCoordinator.EvictGroupMemberAsync(admin, relay, channelId, member, ct);
        if (!success)
            throw new ScenarioExecutionException($"Failed to evict '{step.EvictedMemberAlias}' from group '{step.GroupName}'.");
    }

    private async Task ExecuteRenameGroupAsync(
        RenameGroupStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        Dictionary<string, ISimulatedRelayHost> relayAliasMap,
        Dictionary<string, ChannelId> groupAliasMap,
        CancellationToken ct)
    {
        if (!aliasMap.TryGetValue(step.AdminAlias, out var admin))
            throw new ScenarioExecutionException($"Admin persona '{step.AdminAlias}' not found.");
        if (!groupAliasMap.TryGetValue(step.GroupName, out var channelId))
            throw new ScenarioExecutionException($"Group '{step.GroupName}' not found in scenario.");

        var relay = admin.HomeRelay ?? _worldState.GetAllRelayHosts().FirstOrDefault()
            ?? throw new ScenarioExecutionException("No relay host available for group mutation.");

        var success = await _groupCoordinator.RenameGroupAsync(admin, relay, channelId, step.NewGroupName, ct);
        if (!success)
            throw new ScenarioExecutionException($"Failed to rename group '{step.GroupName}'.");

        groupAliasMap[step.NewGroupName] = channelId;
    }

    private async Task ExecuteAssertGroupEpochAsync(
        AssertGroupEpochAssertion assertion,
        Dictionary<string, ChannelId> groupAliasMap,
        CancellationToken ct)
    {
        if (!groupAliasMap.TryGetValue(assertion.GroupName, out var channelId))
            throw new ScenarioAssertionException($"Group '{assertion.GroupName}' not found in scenario.");

        var details = await _groupCoordinator.GetGroupDetailsAsync(channelId, ct);
        if (details == null)
            throw new ScenarioAssertionException($"Group details for '{assertion.GroupName}' not found.");

        if (details.Value.CurrentEpoch != assertion.ExpectedEpoch)
        {
            throw new ScenarioAssertionException(
                $"Group '{assertion.GroupName}' epoch mismatch. Expected: {assertion.ExpectedEpoch}, Actual: {details.Value.CurrentEpoch}");
        }
    }

    private async Task ExecuteAssertGroupMessageDeliveredAsync(
        AssertGroupMessageDeliveredAssertion assertion,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        Dictionary<string, ChannelId> groupAliasMap,
        CancellationToken ct)
    {
        if (!aliasMap.TryGetValue(assertion.PersonaAlias, out var persona))
            throw new ScenarioAssertionException($"Persona '{assertion.PersonaAlias}' not found.");
        if (!groupAliasMap.TryGetValue(assertion.GroupName, out var channelId))
            throw new ScenarioAssertionException($"Group '{assertion.GroupName}' not found in scenario.");

        var deadline = DateTime.UtcNow.AddSeconds(5);
        bool delivered = false;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var transcript = await _groupCoordinator.GetGroupTranscriptAsync(persona, channelId, ct);
            if (transcript.Any(m => m.Content == assertion.ExpectedText))
            {
                delivered = true;
                break;
            }

            using var loopCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, loopCts.Token);
            try
            {
                await _stateBus.Signals
                    .Where(s => s.NodeId == persona.Id && s.Category == SignalCategory.ChatTranscriptChanged)
                    .FirstAsync(cancellationToken: linked.Token);
            }
            catch (OperationCanceledException) when (loopCts.IsCancellationRequested) { }
        }

        if (!delivered)
        {
            throw new ScenarioAssertionException(
                $"Group message \"{assertion.ExpectedText}\" in '{assertion.GroupName}' was not delivered to '{assertion.PersonaAlias}' after 5 seconds.");
        }
    }

    private async Task ExecuteAssertMemberCannotDecryptAsync(
        AssertMemberCannotDecryptAssertion assertion,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        Dictionary<string, ChannelId> groupAliasMap,
        CancellationToken ct)
    {
        if (!aliasMap.TryGetValue(assertion.PersonaAlias, out var persona))
            throw new ScenarioAssertionException($"Persona '{assertion.PersonaAlias}' not found.");
        if (!groupAliasMap.TryGetValue(assertion.GroupName, out var channelId))
            throw new ScenarioAssertionException($"Group '{assertion.GroupName}' not found in scenario.");

        var transcript = await _groupCoordinator.GetGroupTranscriptAsync(persona, channelId, ct);
        if (transcript.Any(m => m.Content == assertion.ExpectedText))
        {
            throw new ScenarioAssertionException(
                $"Forward secrecy VIOLATION! Evicted member '{assertion.PersonaAlias}' was able to receive/decrypt group message \"{assertion.ExpectedText}\" in '{assertion.GroupName}'.");
        }
    }

    private async Task ExecuteSetDiscoveryVisibilityAsync(
        SetDiscoveryVisibilityStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        CancellationToken ct)
    {
        if (!aliasMap.TryGetValue(step.PersonaAlias, out var persona))
            throw new ScenarioExecutionException($"Persona '{step.PersonaAlias}' not found.");

        var success = await _discoveryCoordinator.SetDiscoveryVisibilityAsync(persona, step.IsDiscoverable, ct);
        if (!success)
            throw new ScenarioExecutionException($"Failed to set discovery visibility for '{step.PersonaAlias}'.");
    }

    private async Task ExecutePublishBlindedLocatorAsync(
        PublishBlindedLocatorStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        Dictionary<string, ISimulatedRelayHost> relayAliasMap,
        CancellationToken ct)
    {
        if (!aliasMap.TryGetValue(step.PersonaAlias, out var persona))
            throw new ScenarioExecutionException($"Persona '{step.PersonaAlias}' not found.");
        if (!relayAliasMap.TryGetValue(step.RelayAlias, out var relay))
            throw new ScenarioExecutionException($"Relay '{step.RelayAlias}' not found.");

        byte[] sharedSecret = ParseSharedSecret(step.SharedSecret);
        await _discoveryCoordinator.PublishBlindedLocatorAsync(persona, relay, sharedSecret, step.RouteDescriptor, ct);
    }

    private async Task ExecuteResolveBlindedLocatorAsync(
        ResolveBlindedLocatorStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        Dictionary<string, ISimulatedRelayHost> relayAliasMap,
        CancellationToken ct)
    {
        if (!aliasMap.TryGetValue(step.PersonaAlias, out var persona))
            throw new ScenarioExecutionException($"Persona '{step.PersonaAlias}' not found.");
        if (!aliasMap.TryGetValue(step.TargetAlias, out var target))
            throw new ScenarioExecutionException($"Target persona '{step.TargetAlias}' not found.");
        if (!relayAliasMap.TryGetValue(step.RelayAlias, out var relay))
            throw new ScenarioExecutionException($"Relay '{step.RelayAlias}' not found.");

        byte[] sharedSecret = ParseSharedSecret(step.SharedSecret);
        await _discoveryCoordinator.ResolveBlindedLocatorAsync(persona, relay, target.PublicIdentityId, sharedSecret, ct);
    }

    private async Task ExecuteQueryNearbyPeersAsync(
        QueryNearbyPeersStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        Dictionary<string, ISimulatedRelayHost> relayAliasMap,
        CancellationToken ct)
    {
        if (!aliasMap.TryGetValue(step.PersonaAlias, out var persona))
            throw new ScenarioExecutionException($"Persona '{step.PersonaAlias}' not found.");
        if (!relayAliasMap.TryGetValue(step.RelayAlias, out var relay))
            throw new ScenarioExecutionException($"Relay '{step.RelayAlias}' not found.");

        await _discoveryCoordinator.QueryNearbyPeersAsync(persona, relay, step.Limit, ct);
    }

    private async Task ExecuteConnectDiscoveredPeerAsync(
        ConnectDiscoveredPeerStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        Dictionary<string, ISimulatedRelayHost> relayAliasMap,
        CancellationToken ct)
    {
        if (!aliasMap.TryGetValue(step.InitiatorAlias, out var initiator))
            throw new ScenarioExecutionException($"Initiator persona '{step.InitiatorAlias}' not found.");
        if (!aliasMap.TryGetValue(step.CandidateAlias, out var candidate))
            throw new ScenarioExecutionException($"Candidate persona '{step.CandidateAlias}' not found.");

        string? relayEndpoint = null;
        if (!string.IsNullOrWhiteSpace(step.RelayAlias) && relayAliasMap.TryGetValue(step.RelayAlias, out var relay))
        {
            relayEndpoint = $"https://127.0.0.1:{relay.BoundPort}";
        }

        var candidateListening = candidate.IsListening.CurrentValue;

        Task signalTask = candidateListening
            ? _stateBus.Signals
                .Where(s => s.NodeId == candidate.Id && s.Category == SignalCategory.PendingHandshakeChanged)
                .FirstAsync(cancellationToken: ct)
            : Task.CompletedTask;

        var success = await _discoveryCoordinator.ConnectDiscoveredPeerAsync(initiator, candidate.PublicIdentityId, relayEndpoint, ct);
        if (!success)
            throw new ScenarioExecutionException($"Failed to connect discovered peer '{step.CandidateAlias}' from '{step.InitiatorAlias}'.");

        if (candidateListening)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            await signalTask.WaitAsync(linkedCts.Token);
        }
    }

    private void ExecuteAssertBlindedLocatorResolved(
        AssertBlindedLocatorResolvedAssertion assertion,
        Dictionary<string, ISimulatedPersonaHost> aliasMap)
    {
        if (!aliasMap.TryGetValue(assertion.ObserverAlias, out var observer))
            throw new ScenarioAssertionException($"Observer persona '{assertion.ObserverAlias}' not found.");
        if (!aliasMap.TryGetValue(assertion.TargetAlias, out var target))
            throw new ScenarioAssertionException($"Target persona '{assertion.TargetAlias}' not found.");

        var matches = _discoveryCoordinator.GetResolvedMatches(observer.PublicIdentityId);
        if (matches.Count == 0)
        {
            throw new ScenarioAssertionException(
                $"Blinded locator assertion failed: '{assertion.ObserverAlias}' has 0 resolved matches (expected match for '{assertion.TargetAlias}').");
        }
    }

    private void ExecuteAssertNearbyCandidateCount(
        AssertNearbyCandidateCountAssertion assertion,
        Dictionary<string, ISimulatedPersonaHost> aliasMap)
    {
        if (!aliasMap.TryGetValue(assertion.ObserverAlias, out var observer))
            throw new ScenarioAssertionException($"Observer persona '{assertion.ObserverAlias}' not found.");

        var candidates = _discoveryCoordinator.GetLastDiscoveredPeers(observer.PublicIdentityId);
        if (candidates.Count != assertion.ExpectedCount)
        {
            throw new ScenarioAssertionException(
                $"Nearby candidate count mismatch on '{assertion.ObserverAlias}'. Expected: {assertion.ExpectedCount}, Actual: {candidates.Count}");
        }
    }

    private static byte[] ParseSharedSecret(string input)
    {
        var trimmed = input?.Trim() ?? string.Empty;
        if (trimmed.Length >= 32 && trimmed.All(Uri.IsHexDigit))
        {
            try
            {
                return Convert.FromHexString(trimmed);
            }
            catch { }
        }
        var utf8 = System.Text.Encoding.UTF8.GetBytes(trimmed);
        if (utf8.Length >= 16)
        {
            return utf8;
        }
        return System.Security.Cryptography.SHA256.HashData(utf8);
    }

    private async Task ExecuteInitiateFileTransferAsync(
        InitiateFileTransferStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        CancellationToken ct)
    {
        if (!aliasMap.TryGetValue(step.InitiatorAlias, out var initiator))
            throw new ScenarioExecutionException($"Initiator persona '{step.InitiatorAlias}' not found.");

        var recipientIds = new List<SimulatedNodeId>();
        foreach (var rAlias in step.RecipientAliases)
        {
            if (!aliasMap.TryGetValue(rAlias, out var recipient))
                throw new ScenarioExecutionException($"Recipient persona '{rAlias}' not found.");
            recipientIds.Add(recipient.Id);
        }

        var sessionId = await _swarmCoordinator.InitiateTransferAsync(initiator.Id, recipientIds, step.PresetSize, ct);
        _lastSwarmSessionId = sessionId;
        if (sessionId == Guid.Empty)
        {
            throw new ScenarioExecutionException($"Failed to initiate file transfer from '{step.InitiatorAlias}'.");
        }
    }

    private async Task ExecuteAcceptFileTransferAsync(
        AcceptFileTransferStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        CancellationToken ct)
    {
        if (!aliasMap.TryGetValue(step.RecipientAlias, out var recipient))
            throw new ScenarioExecutionException($"Recipient persona '{step.RecipientAlias}' not found.");

        var swarm = GetTargetSwarm();

        var success = await _swarmCoordinator.AcceptTransferAsync(recipient.Id, swarm.SessionId);
        if (!success)
        {
            throw new ScenarioExecutionException($"Failed to accept file transfer on persona '{step.RecipientAlias}'.");
        }
    }

    private async Task ExecuteInjectLateDownloaderAsync(
        InjectLateDownloaderStep step,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        CancellationToken ct)
    {
        if (!aliasMap.TryGetValue(step.LateJoinerAlias, out var lateJoiner))
            throw new ScenarioExecutionException($"Late joiner persona '{step.LateJoinerAlias}' not found.");

        var swarm = GetTargetSwarm();

        var success = await _swarmCoordinator.InjectLateDownloaderAsync(swarm.SessionId, lateJoiner.Id, step.ProgressThreshold);
        if (!success)
        {
            throw new ScenarioExecutionException($"Failed to inject late downloader '{step.LateJoinerAlias}'.");
        }
    }

    private async Task ExecutePauseFileTransferAsync(
        PauseFileTransferStep step,
        CancellationToken ct)
    {
        var swarm = GetTargetSwarm();

        await _swarmCoordinator.PauseTransferAsync(swarm.SessionId);
    }

    private async Task ExecuteResumeFileTransferAsync(
        ResumeFileTransferStep step,
        CancellationToken ct)
    {
        var swarm = GetTargetSwarm();

        await _swarmCoordinator.ResumeTransferAsync(swarm.SessionId);
    }

    private async Task ExecuteCancelFileTransferAsync(
        CancelFileTransferStep step,
        CancellationToken ct)
    {
        var swarm = GetTargetSwarm();

        await _swarmCoordinator.CancelTransferAsync(swarm.SessionId);
    }

    private async Task ExecuteAssertFileTransferProgressAsync(
        AssertFileTransferProgressAssertion assertion,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        CancellationToken ct)
    {
        if (!aliasMap.TryGetValue(assertion.PersonaAlias, out var persona))
            throw new ScenarioAssertionException($"Persona '{assertion.PersonaAlias}' not found.");

        var swarm = GetTargetSwarm();

        var participant = swarm.Participants.FirstOrDefault(p => p.NodeId == persona.Id)
            ?? throw new ScenarioAssertionException($"Persona '{assertion.PersonaAlias}' is not a participant in swarm.");

        double progress = participant.TotalChunks > 0 ? (double)participant.CompletedChunks / participant.TotalChunks : 0.0;
        if (progress < assertion.MinProgressRatio)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            await _stateBus.Signals
                .Where(s => s.NodeId == persona.Id &&
                            s.Category == SignalCategory.SwarmProgressChanged &&
                            s.Payload is SwarmChunkProgressSignal sig &&
                            sig.TotalChunks > 0 &&
                            ((double)sig.CompletedChunks / sig.TotalChunks) >= assertion.MinProgressRatio)
                .FirstAsync(cancellationToken: linkedCts.Token);
        }
    }

    private async Task ExecuteAssertFileTransferCompletedAsync(
        AssertFileTransferCompletedAssertion assertion,
        Dictionary<string, ISimulatedPersonaHost> aliasMap,
        CancellationToken ct)
    {
        if (!aliasMap.TryGetValue(assertion.PersonaAlias, out var persona))
            throw new ScenarioAssertionException($"Persona '{assertion.PersonaAlias}' not found.");

        var swarm = GetTargetSwarm();

        var participant = swarm.Participants.FirstOrDefault(p => p.NodeId == persona.Id)
            ?? throw new ScenarioAssertionException($"Persona '{assertion.PersonaAlias}' is not a participant in swarm.");

        if (!participant.IsComplete)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            await _stateBus.Signals
                .Where(s => s.NodeId == persona.Id && s.Category == SignalCategory.FileTransferCompleted)
                .FirstAsync(cancellationToken: linkedCts.Token);
        }
    }

    private void ExecuteAssertFileIntegrityVerified(
        AssertFileIntegrityVerifiedAssertion assertion,
        Dictionary<string, ISimulatedPersonaHost> aliasMap)
    {
        if (!aliasMap.TryGetValue(assertion.PersonaAlias, out var persona))
            throw new ScenarioAssertionException($"Persona '{assertion.PersonaAlias}' not found.");

        var swarm = GetTargetSwarm();

        string targetFilePath = Path.Combine(persona.NodeDownloadsDirectory, swarm.Preset.RelativeFileName);
        if (!File.Exists(targetFilePath))
        {
            throw new ScenarioAssertionException($"Downloaded file not found on disk at '{targetFilePath}'.");
        }

        using var sha256 = System.Security.Cryptography.SHA256.Create();
        using var fs = File.OpenRead(targetFilePath);
        byte[] computedHash = sha256.ComputeHash(fs);
        string computedHex = Convert.ToHexString(computedHash).ToLowerInvariant();

        if (!string.Equals(computedHex, swarm.Preset.Sha256Hex, StringComparison.OrdinalIgnoreCase))
        {
            throw new ScenarioAssertionException(
                $"SHA-256 integrity verification failed for '{assertion.PersonaAlias}'. Expected: {swarm.Preset.Sha256Hex}, Got: {computedHex}");
        }
    }

    private ActiveSwarmState GetTargetSwarm()
    {
        if (_lastSwarmSessionId.HasValue)
        {
            var swarm = _swarmCoordinator.GetSwarm(_lastSwarmSessionId.Value);
            if (swarm != null) return swarm;
        }
        return _swarmCoordinator.GetLatestSwarm()
            ?? throw new ScenarioExecutionException("No active swarm transfer found.");
    }
}
