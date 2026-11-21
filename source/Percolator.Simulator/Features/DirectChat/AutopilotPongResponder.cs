using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Application.Ports;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.Ports;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;
using R3;

namespace Percolator.Simulator.Features.DirectChat;

internal sealed class AutopilotPongResponder : IDisposable
{
    private readonly ISimulatedWorldStateService _worldState;
    private readonly ISimulatorStateBus _stateBus;
    private readonly DirectChatCoordinator _chatCoordinator;
    private readonly ISimulatorLogSink _logSink;
    private readonly ConcurrentDictionary<SimulatedNodeId, bool> _autoPongEnabled = new();
    private readonly ConcurrentDictionary<SimulatedNodeId, int> _pongCounters = new();
    private readonly ConcurrentDictionary<SimulatedNodeId, Guid> _lastRespondedPayload = new();
    private readonly IDisposable _subscription;

    public AutopilotPongResponder(
        ISimulatedWorldStateService worldState,
        ISimulatorStateBus stateBus,
        DirectChatCoordinator chatCoordinator,
        ISimulatorLogSink logSink)
    {
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _stateBus = stateBus ?? throw new ArgumentNullException(nameof(stateBus));
        _chatCoordinator = chatCoordinator ?? throw new ArgumentNullException(nameof(chatCoordinator));
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));

        _subscription = _stateBus.Signals
            .Where(s => s.Category == SignalCategory.ChatTranscriptChanged)
            .Subscribe(signal => OnChatTranscriptChanged(signal.NodeId));
    }

    public void SetAutoPongEnabled(SimulatedNodeId nodeId, bool enabled)
    {
        _autoPongEnabled[nodeId] = enabled;
    }

    public bool IsAutoPongEnabled(SimulatedNodeId nodeId)
    {
        return _autoPongEnabled.TryGetValue(nodeId, out var enabled) && enabled;
    }

    private void OnChatTranscriptChanged(SimulatedNodeId nodeId)
    {
        if (!IsAutoPongEnabled(nodeId)) return;

        // Run async in background task with 250ms delay
        _ = Task.Run(async () =>
        {
            try
            {
                var host = _worldState.GetPersonaHost(nodeId);
                if (host == null) return;

                using var scope = host.Services.CreateScope();
                var chatQuery = scope.ServiceProvider.GetRequiredService<ISimulatorChatQueryService>();
                var contactQuery = scope.ServiceProvider.GetRequiredService<IPeerContactQueryService>();

                // Query all channels or find latest incoming message
                // In direct chat, check messages across contacts and in-world peers for this host
                var activeContactIds = new HashSet<PublicIdentityId>();
                try
                {
                    var contacts = await contactQuery.GetContactSummariesAsync(host.PublicIdentityId);
                    foreach (var c in contacts.Where(c => c.State == ContactState.Active))
                    {
                        activeContactIds.Add(c.IdentityId);
                    }
                }
                catch
                {
                    // Ignore query failures on initial startup
                }

                foreach (var peer in _worldState.Personas.Where(p => p.Id != nodeId))
                {
                    var pHost = _worldState.GetPersonaHost(peer.Id);
                    if (pHost != null)
                    {
                        activeContactIds.Add(pHost.PublicIdentityId);
                    }
                }

                SimulatorChatMessageReadModel? latestIncoming = null;
                PublicIdentityId? senderIdentityId = null;

                foreach (var peerId in activeContactIds)
                {
                    var channelId = ChannelIdUtility.DeterministicFor(host.PublicIdentityId, peerId);
                    var messages = await chatQuery.GetTranscriptAsync(channelId);
                    var candidate = messages
                        .Where(m => m.AuthorId != host.PublicIdentityId)
                        .OrderByDescending(m => m.TimestampUnixMs)
                        .FirstOrDefault();

                    if (candidate != null && (latestIncoming == null || candidate.TimestampUnixMs > latestIncoming.TimestampUnixMs))
                    {
                        latestIncoming = candidate;
                        senderIdentityId = peerId;
                    }
                }

                if (latestIncoming == null || senderIdentityId == null) return;

                // Deduplicate responses: don't reply twice to the same payload
                if (_lastRespondedPayload.TryGetValue(nodeId, out var lastId) && lastId == latestIncoming.MessageId)
                {
                    return;
                }

                _lastRespondedPayload[nodeId] = latestIncoming.MessageId;

                // 250ms cadence per plan spec
                await Task.Delay(250);

                var count = _pongCounters.AddOrUpdate(nodeId, 1, (_, c) => c + 1);
                var snippet = latestIncoming.Content.Length > 20
                    ? latestIncoming.Content[..20] + "..."
                    : latestIncoming.Content;

                var replyText = $"Pong #{count} (re: {snippet})";
                var senderHost = _worldState.FindPersonaHostByIdentity(senderIdentityId.Value);
                var senderName = senderHost?.DisplayName ?? senderIdentityId.Value.ToString()[..8];

                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Information,
                    host.DisplayName,
                    host.Id,
                    "Autopilot",
                    $"Autopilot triggered. Sending '{replyText}' to '{senderName}'"));

                await _chatCoordinator.SendDirectMessageAsync(host.Id, senderIdentityId.Value, replyText);
            }
            catch (Exception ex)
            {
                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Error,
                    null,
                    nodeId,
                    "Autopilot",
                    $"Error generating autopilot pong reply: {ex.Message}"));
            }
        });
    }

    public void Dispose()
    {
        _subscription.Dispose();
    }
}
