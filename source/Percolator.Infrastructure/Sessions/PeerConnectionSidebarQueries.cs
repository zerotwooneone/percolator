using Microsoft.EntityFrameworkCore;
using Percolator.Application.Sessions;
using Percolator.Cryptography;
using Percolator.Infrastructure.Cryptography;
using Percolator.Infrastructure.Identity;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Sessions;

public sealed class PeerConnectionSidebarQueries : IPeerConnectionSidebarQueries
{
    private readonly PercolatorDbContext _db;
    private readonly IClock _clock;

    public PeerConnectionSidebarQueries(PercolatorDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<IReadOnlyList<SidebarPeerConnectionDto>> LoadSidebarConnectionsAsync(int selfIdentityId, CancellationToken cancellationToken = default)
    {
        var nowUtc = _clock.UtcNow;

        // Load established secure sessions
        var establishedSessions = await _db.Sessions
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(s => s.SelfIdentityId == selfIdentityId)
            .Join(
                _db.PeerIdentities.AsNoTracking(),
                session => session.RemotePeerId,
                peer => peer.PeerId,
                (session, peer) => new { session, peer })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Load direct sessions for direct vs relay determination
        var directSessionPeerIds = await _db.DirectSessions
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(d => d.SelfIdentityId == selfIdentityId)
            .Select(d => d.RemotePeerId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var directPeerIdSet = new HashSet<Guid>(directSessionPeerIds);

        // Load pending outbound invitations (materialize first for SQLite DateTimeOffset filtering)
        var sentInvitationCandidates = await _db.SentInvitations
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(i => i.SelfIdentityId == selfIdentityId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Filter unexpired invitations in-memory (SQLite limitation)
        var unexpiredInvitations = sentInvitationCandidates
            .Where(i => i.ExpiresAtUtc > nowUtc)
            .ToList();

        // Build established session DTOs
        var establishedDtos = new List<SidebarPeerConnectionDto>();
        foreach (var item in establishedSessions)
        {
            var status = directPeerIdSet.Contains(item.session.RemotePeerId)
                ? SidebarPeerConnectionStatus.Direct
                : SidebarPeerConnectionStatus.Relay;

            var displayName = item.peer.Name ?? item.session.RemotePeerId.ToString()[..8];
            var initials = ComputeInitials(displayName);

            establishedDtos.Add(new SidebarPeerConnectionDto
            {
                SelfIdentityId = selfIdentityId,
                KeyType = SidebarPeerConnectionKeyType.SecureSession,
                KeyValue = item.session.SessionId,
                PeerId = item.session.RemotePeerId,
                DisplayName = displayName,
                Initials = initials,
                Status = status,
                RelayHostPeerId = null,
                LastActivityUtc = item.session.LastUsedAtUtc
            });
        }

        // Pre-fetch peer identities for all pending invitations to avoid N+1 queries
        var targetPeerIds = unexpiredInvitations
            .Where(i => i.TargetPeerId.HasValue)
            .Select(i => i.TargetPeerId!.Value)
            .Distinct()
            .ToList();

        var peerIdentities = targetPeerIds.Count > 0
            ? await _db.PeerIdentities
                .AsNoTracking()
                .Where(p => targetPeerIds.Contains(p.PeerId))
                .ToDictionaryAsync(p => p.PeerId, cancellationToken)
                .ConfigureAwait(false)
            : new Dictionary<Guid, PeerIdentityDbo>();

        // Build pending outbound DTOs
        var pendingDtos = new List<SidebarPeerConnectionDto>();
        foreach (var invitation in unexpiredInvitations)
        {
            // Parse correlation GUID from string
            if (!Guid.TryParse(invitation.RequestCorrelationId, out var correlationGuid))
                continue;

            // Suppress pending if established session exists for the same remote peer
            if (invitation.TargetPeerId.HasValue)
            {
                var hasEstablishedSession = establishedDtos.Any(d => d.PeerId == invitation.TargetPeerId.Value);
                if (hasEstablishedSession)
                    continue;
            }

            // Derive display name
            string displayName;
            if (!string.IsNullOrWhiteSpace(invitation.TargetDisplayName))
            {
                displayName = invitation.TargetDisplayName;
            }
            else if (invitation.TargetPeerId.HasValue)
            {
                displayName = peerIdentities.TryGetValue(invitation.TargetPeerId.Value, out var peer)
                    ? peer.Name ?? invitation.TargetPeerId.Value.ToString()[..8]
                    : invitation.TargetPeerId.Value.ToString()[..8];
            }
            else if (!string.IsNullOrWhiteSpace(invitation.TargetEndpointHost) && invitation.TargetEndpointPort.HasValue)
            {
                displayName = $"{invitation.TargetEndpointHost}:{invitation.TargetEndpointPort}";
            }
            else
            {
                displayName = correlationGuid.ToString("N")[..8];
            }

            var initials = ComputeInitials(displayName);

            pendingDtos.Add(new SidebarPeerConnectionDto
            {
                SelfIdentityId = selfIdentityId,
                KeyType = SidebarPeerConnectionKeyType.PendingCorrelation,
                KeyValue = correlationGuid,
                PeerId = invitation.TargetPeerId,
                DisplayName = displayName,
                Initials = initials,
                Status = SidebarPeerConnectionStatus.PendingOutbound,
                RelayHostPeerId = invitation.InviteRelayHostPeerId,
                LastActivityUtc = invitation.CreatedAtUtc
            });
        }

        // Combine and return
        var result = new List<SidebarPeerConnectionDto>(establishedDtos.Count + pendingDtos.Count);
        result.AddRange(establishedDtos);
        result.AddRange(pendingDtos);
        return result;
    }

    private static string ComputeInitials(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return "?";

        var parts = displayName.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
            return $"{char.ToUpper(parts[0][0])}{char.ToUpper(parts[1][0])}";

        return displayName.Length >= 2
            ? $"{char.ToUpper(displayName[0])}{char.ToUpper(displayName[1])}"
            : displayName.ToUpper();
    }
}
