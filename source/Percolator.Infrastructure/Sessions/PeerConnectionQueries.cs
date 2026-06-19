using Microsoft.Extensions.Logging;
using Percolator.Application.Cryptography;
using Percolator.Application.Sessions;
using Percolator.Identity;

namespace Percolator.Infrastructure.Sessions;

internal sealed class PeerConnectionQueries : IPeerConnectionQueries
{
    private readonly IPendingHandshakeQueries _pendingHandshakeQueries;
    private readonly ILogger<PeerConnectionQueries> _logger;

    public PeerConnectionQueries(
        IPendingHandshakeQueries pendingHandshakeQueries,
        ILogger<PeerConnectionQueries> logger)
    {
        _pendingHandshakeQueries = pendingHandshakeQueries;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PendingInboundSnapshot>> LoadPendingInboundAsync(SelfId selfIdentityId, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning(
            "LoadPendingInboundAsync(selfIdentityId={SelfIdentityId}) currently ignores identity scoping due to implicit query filters; multi-identity is not implemented yet.",
            selfIdentityId);

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
                RelayEndpoint: pending.RelayEndpoint,
                SelfIdentityId: selfIdentityId));
        }

        return snapshots;
    }
}
