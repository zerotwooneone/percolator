using Desktop.Wpf.Features.Sessions.Models;
using Percolator.Application.Cryptography;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;

namespace Desktop.Wpf.Features.Sessions.Queries;

internal sealed class PeerConnectionQueries : IPeerConnectionQueries
{
    private readonly ISessionRepository _sessions;
    private readonly IPeerIdentityRepository _peers;
    private readonly IDirectSessionRepository _directSessions;
    private readonly IPendingHandshakeQueries _pendingHandshakeQueries;

    public PeerConnectionQueries(
        ISessionRepository sessions,
        IPeerIdentityRepository peers,
        IDirectSessionRepository directSessions,
        IPendingHandshakeQueries pendingHandshakeQueries)
    {
        _sessions = sessions;
        _peers = peers;
        _directSessions = directSessions;
        _pendingHandshakeQueries = pendingHandshakeQueries;
    }

    public async Task<IReadOnlyList<PeerConnectionStateSnapshot>> LoadAllConnectionsAsync(int selfIdentityId, CancellationToken cancellationToken = default)
    {
        var sessions = await _sessions.GetAllActiveAsync(selfIdentityId, cancellationToken).ConfigureAwait(false);
        
        IReadOnlyList<DirectSession> direct;
        try
        {
            direct = await _directSessions.ListAsync(selfIdentityId).ConfigureAwait(false);
        }
        catch
        {
            direct = Array.Empty<DirectSession>();
        }

        var directPeers = new HashSet<Guid>(direct.Select(x => x.RemotePeerId.Value));
        var snapshots = new List<PeerConnectionStateSnapshot>(sessions.Count);

        foreach (var session in sessions)
        {
            var peerId = new Percolator.Identity.PeerId(session.RemotePeerId.Value);
            var peer = await _peers.GetByIdAsync(peerId, cancellationToken).ConfigureAwait(false);
            var displayName = peer?.DisplayName?.Value ?? session.RemotePeerId.Value.ToString()[..8];
            var initials = ComputeInitials(displayName);

            var isRelayed = !directPeers.Contains(session.RemotePeerId.Value);
            var status = isRelayed ? PeerConnectionStatus.Relay : PeerConnectionStatus.Direct;

            snapshots.Add(new PeerConnectionStateSnapshot(
                Key: PeerConnectionKey.FromSessionId(session.Id.Value),
                PeerId: session.RemotePeerId.Value,
                DisplayName: displayName,
                Initials: initials,
                Status: status,
                RelayHostPeerId: null,
                LastActivityUtc: session.LastUsedAtUtc));
        }

        return snapshots;
    }

    public async Task<IReadOnlyList<PendingInboundSnapshot>> LoadPendingInboundAsync(CancellationToken cancellationToken = default)
    {
        var snapshots = new List<PendingInboundSnapshot>();
        
        await foreach (var pending in _pendingHandshakeQueries.EnumerateOpenAsync(cancellationToken).ConfigureAwait(false))
        {
            snapshots.Add(new PendingInboundSnapshot(
                PendingSessionId: pending.Id.Value,
                RequestCorrelationId: pending.RequestCorrelationId.Value,
                PeerId: pending.RemotePeer.Value,
                PeerName: pending.PeerName,
                InviterFingerprintHex: pending.InviterFingerprintHex,
                CreatedAtUtc: pending.CreatedAtUtc,
                ExpiresAtUtc: pending.ExpiresAtUtc,
                IsRelayed: pending.IsRelayed,
                RelayPeerId: pending.RelayPeer?.Value,
                RelayPeerName: pending.RelayPeerName,
                RelayEndpoint: pending.RelayEndpoint));
        }

        return snapshots;
    }

    private static string ComputeInitials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
            return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();
        return (parts[0][0].ToString() + parts[^1][0].ToString()).ToUpperInvariant();
    }
}
