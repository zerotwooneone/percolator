using Desktop.Wpf.Features.Simulator.Models;
using Microsoft.Extensions.Logging;
using Percolator.Cryptography;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatorRelayAutoDeliverService : ISimulatorRelayAutoDeliverService, IDisposable
{
    private static readonly Guid MainNodeSentinelPeerId = new("88880000-0000-0000-0000-000000000000");

    private readonly ISimulatorStateService _state;
    private readonly ISimulatorRelayDeliveryService _delivery;
    private readonly ISimulatorDiagnosticsService _diagnostics;
    private readonly ILogger<SimulatorRelayAutoDeliverService> _logger;
    private readonly ISimulatorDelay _delay;

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public SimulatorRelayAutoDeliverService(
        ISimulatorStateService state,
        ISimulatorRelayDeliveryService delivery,
        ISimulatorDiagnosticsService diagnostics,
        ISimulatorDelay delay,
        ILogger<SimulatorRelayAutoDeliverService> logger)
    {
        _state = state;
        _delivery = delivery;
        _diagnostics = diagnostics;
        _delay = delay;
        _logger = logger;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null && !_loop.IsCompleted)
            {
                return;
            }

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => LoopAsync(_cts.Token));
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
            _loop = null;
        }

        try { cts?.Cancel(); } catch { }
        try { cts?.Dispose(); } catch { }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _delay.DelayAsync(TimeSpan.FromMilliseconds(350), ct).ConfigureAwait(false);

                var relays = _state.Relays.ToArray();
                foreach (var relay in relays)
                {
                    ct.ThrowIfCancellationRequested();

                    if (!relay.AutoDeliverEnabled.CurrentValue)
                    {
                        continue;
                    }

                    await TryDeliverOneAsync(relay, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[simulator] Relay auto-deliver loop error");
            }
        }
    }

    private async Task TryDeliverOneAsync(SimulatedRelayModel relay, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        RelayMessage? next;
        try
        {
            next = relay.MessageQueue
                .Select(kvp => kvp.Value)
                .OrderBy(x => x.EnqueuedUtc)
                .FirstOrDefault();
        }
        catch
        {
            return;
        }

        if (next is null)
        {
            return;
        }

        if (next is OutboundRelayMessage)
        {
            var sid = TryGetRelayHostToMainSessionId(relay.RelayHostPeerId);
            if (sid is null)
            {
                return;
            }

            var delivered = await _state
                .DeliverRelayUpstreamToMainByAckIdAsync(relay.RelayHostPeerId, sid, next.AckId, ct)
                .ConfigureAwait(false);

            if (delivered)
            {
                _diagnostics.Emit(
                    SimulatorDiagnosticEventType.RelayDelivered,
                    $"Relay deliver -> main: {(next.DebugType ?? "opaque")}",
                    relayHostPeerId: relay.RelayHostPeerId,
                    ackId: next.AckId);
            }

            return;
        }

        if (next is InboundRelayMessage inbound)
        {
            Guid? recipientPeerId = null;
            try
            {
                var resolved = await _state.TryGetPeerIdByIdentityPkhAsync(inbound.TargetPkh, ct).ConfigureAwait(false);
                recipientPeerId = resolved;
            }
            catch
            {
                return;
            }

            if (!recipientPeerId.HasValue)
            {
                return;
            }

            try
            {
                await _delivery.DeliverToPeerAsync(
                        relayHostPeerId: relay.RelayHostPeerId,
                        recipientPeerId: recipientPeerId.Value,
                        ackId: inbound.AckId,
                        opaqueBytes: inbound.OpaqueBytes,
                        debugType: inbound.DebugType,
                        cancellationToken: ct)
                    .ConfigureAwait(false);

                _ = await _state.DeleteRelayMessageByAckIdAsync(relay.RelayHostPeerId, inbound.AckId, ct).ConfigureAwait(false);

                _diagnostics.Emit(
                    SimulatorDiagnosticEventType.RelayDelivered,
                    $"Relay deliver -> {recipientPeerId.Value.ToString()[..8]}: {(inbound.DebugType ?? "opaque")}",
                    peerId: recipientPeerId.Value,
                    relayHostPeerId: relay.RelayHostPeerId,
                    ackId: inbound.AckId);
            }
            catch
            {
                // leave message in queue
            }
        }
    }

    private SessionId? TryGetRelayHostToMainSessionId(Guid relayHostPeerId)
    {
        var peer = _state.Peers.FirstOrDefault(p => p.PeerId == relayHostPeerId);
        if (peer is null) return null;

        var match = peer.Sessions
            .Select(kv => kv.Value)
            .FirstOrDefault(s => s.RemotePeerId.Value == MainNodeSentinelPeerId);

        return match?.Id;
    }

    public void Dispose()
    {
        Stop();
    }
}
