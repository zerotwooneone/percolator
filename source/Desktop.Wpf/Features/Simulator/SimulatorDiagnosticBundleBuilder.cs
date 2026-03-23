using System.Text.Json;

namespace Desktop.Wpf.Features.Simulator;

public interface ISimulatorDiagnosticBundleBuilder
{
    Task<string> BuildJsonAsync(CancellationToken ct = default);
}

public sealed class SimulatorDiagnosticBundleBuilder : ISimulatorDiagnosticBundleBuilder
{
    private readonly ISimulatorDiagnosticsService _diagnostics;
    private readonly ISimulatorStateService _state;

    public SimulatorDiagnosticBundleBuilder(ISimulatorDiagnosticsService diagnostics, ISimulatorStateService state)
    {
        _diagnostics = diagnostics;
        _state = state;
    }

    public async Task<string> BuildJsonAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var peerModels = _state.Peers.ToArray();
        var peerSnapshots = _state.SnapshotPeers();

        var peers = peerModels
            .Select(p =>
            {
                var snap = peerSnapshots.FirstOrDefault(x => x.PeerId == p.PeerId);
                return new
                {
                    p.PeerId,
                    DisplayName = snap?.DisplayName ?? p.DisplayName.CurrentValue,
                    IsOnline = snap?.IsOnline ?? p.IsOnline.CurrentValue,
                    IsRelayCapable = snap?.IsRelayCapable ?? p.IsRelayCapable.CurrentValue,
                    ConnectionMode = (snap?.ConnectionMode ?? ConnectionMode.Direct).ToString(),
                    RelayPeerId = snap?.RelayPeerId
                };
            })
            .ToList();

        var relayQueueSummary = peerSnapshots
            .Where(p => p.IsRelayCapable)
            .Select(p => new
            {
                RelayHostPeerId = p.PeerId,
                RelayHostName = string.IsNullOrWhiteSpace(p.DisplayName) ? p.PeerId.ToString()[..8] : p.DisplayName,
                OpaqueQueueCount = p.RelayOpaqueQueueItems.Count,
                PreKeyBundleCount = p.RelayPreKeyBundleCount
            })
            .ToList();

        var recentEvents = _diagnostics
            .GetRecentEvents(500)
            .Select(e => new
            {
                e.TimestampUtc,
                e.EventType,
                e.Message,
                e.PeerId,
                e.RelayHostPeerId,
                e.AckId,
                e.ContextTag
            })
            .ToList();

        var sessionSummaries = new List<object>();
        foreach (var p in peerModels)
        {
            ct.ThrowIfCancellationRequested();
            var store = await _state.TryGetRuntimeStoreAsync(p.PeerId, ct).ConfigureAwait(false);
            if (store is null) continue;

            foreach (var s in store.Sessions)
            {
                sessionSummaries.Add(new
                {
                    LocalPeerId = p.PeerId,
                    RemotePeerId = s.RemotePeerId,
                    s.SessionId,
                    s.ProtocolVersion,
                    SendCounter = s.SendCounter,
                    RecvCounter = s.RecvCounter,
                    s.SkippedKeysCount,
                    s.CreatedAtUtc,
                    s.LastUsedAtUtc
                });
            }
        }

        var bundle = new
        {
            Version = 1,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Peers = peers,
            RelayQueueSummary = relayQueueSummary,
            RecentEvents = recentEvents,
            SessionSummaries = sessionSummaries
        };

        return JsonSerializer.Serialize(bundle, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }
}
