using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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

        var peers = _state.Peers
            .Select(p => new
            {
                p.PeerId,
                p.DisplayName,
                p.IsOnline,
                IsRelayCapable = p.Relay?.IsRelayCapable == true,
                ConnectionMode = p.Connection?.Mode.ToString(),
                RelayPeerId = p.Connection?.RelayPeerId
            })
            .ToList();

        var relayQueueSummary = _state.Peers
            .Where(p => p.Relay?.IsRelayCapable == true)
            .Select(p => new
            {
                RelayHostPeerId = p.PeerId,
                RelayHostName = p.DisplayName,
                OpaqueQueueCount = p.Relay?.OpaqueQueue?.Items?.Count ?? 0,
                PreKeyBundleCount = p.Relay?.PreKeyStore?.PublishedBundles?.Count ?? 0
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
        foreach (var p in _state.Peers)
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
