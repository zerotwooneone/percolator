using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

                // Query all channels or find latest incoming message
                // In direct chat, check messages across channels for this host
                var peers = _worldState.Personas.Where(p => p.Id != nodeId).ToList();
                SimulatorChatMessageReadModel? latestIncoming = null;
                ISimulatedPersonaHost? senderHost = null;

                foreach (var peer in peers)
                {
                    var peerHost = _worldState.GetPersonaHost(peer.Id);
                    if (peerHost == null) continue;

                    var channelId = ChannelIdUtility.DeterministicFor(host.PublicIdentityId, peerHost.PublicIdentityId);
                    var messages = await chatQuery.GetTranscriptAsync(channelId);
                    var candidate = messages
                        .Where(m => m.AuthorId != host.PublicIdentityId)
                        .OrderByDescending(m => m.TimestampUnixMs)
                        .FirstOrDefault();

                    if (candidate != null && (latestIncoming == null || candidate.TimestampUnixMs > latestIncoming.TimestampUnixMs))
                    {
                        latestIncoming = candidate;
                        senderHost = peerHost;
                    }
                }

                if (latestIncoming == null || senderHost == null) return;

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

                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Information,
                    host.DisplayName,
                    host.Id,
                    "Autopilot",
                    $"Autopilot triggered. Sending '{replyText}' to '{senderHost.DisplayName}'"));

                await _chatCoordinator.SendDirectMessageAsync(host.Id, senderHost.Id, replyText);
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
