using System.Linq;
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

        var peers = peerModels
            .Select(p =>
            {
                return new
                {
                    p.PeerId,
                    DisplayName = p.DisplayName.CurrentValue ?? p.PeerId.ToString()[..8],
                    IsRelayCapable = p.IsRelayCapable.CurrentValue,
                    ConnectionMode = p.ConnectionMode.CurrentValue.ToString(),
                    RelayPeerId = p.RelayPeerId.CurrentValue
                };
            })
            .ToList();

        var relayByHostId = _state.Relays.ToDictionary(r => r.RelayHostPeerId);
        var relayQueueSummary = peerModels
            .Where(p => p.IsRelayCapable.CurrentValue)
            .Select(p => new
            {
                RelayHostPeerId = p.PeerId,
                RelayHostName = string.IsNullOrWhiteSpace(p.DisplayName.CurrentValue) ? p.PeerId.ToString()[..8] : p.DisplayName.CurrentValue,
                OpaqueQueueCount = relayByHostId.TryGetValue(p.PeerId, out var relay)
                    ? relay.MessageQueue.Count
                    : 0,
                PreKeyBundleCount = 0
            })
            .ToList();

        var recentEvents = _diagnostics
            .Events
            .TakeLast(500)
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
            await Task.CompletedTask.ConfigureAwait(false);
            foreach (var s in p.Sessions.Select(kv => kv.Value))
            {
                sessionSummaries.Add(new
                {
                    LocalPeerId = p.PeerId,
                    RemotePeerId = s.RemotePeerId.Value,
                    SessionId = s.Id.Value,
                    ProtocolVersion = s.ProtocolVersion.Value,
                    SendCounter = s.State.SendingCounter,
                    RecvCounter = s.State.ReceivingCounter,
                    SkippedKeysCount = s.SkippedKeysCount,
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
