using Microsoft.EntityFrameworkCore;
using Percolator.Application.Apps.Chat.Queries;
using Percolator.Application.Network;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Infrastructure.Persistence;
using Percolator.Network;
using Percolator.Identity;

namespace Percolator.Infrastructure.Chat;

public sealed class SqliteConversationMemberQueries : IConversationMemberQueries
{
    private readonly PercolatorDbContext _context;
    private readonly IPeerRoutingProfileRepository _routingProfileRepository;
    private readonly IProfileRoutePlanner _routePlanner;
    private readonly IPeerPublicSigningKeyStore _keyStore;

    public SqliteConversationMemberQueries(
        PercolatorDbContext context,
        IPeerRoutingProfileRepository routingProfileRepository,
        IProfileRoutePlanner routePlanner,
        IPeerPublicSigningKeyStore keyStore)
    {
        _context = context;
        _routingProfileRepository = routingProfileRepository;
        _routePlanner = routePlanner;
        _keyStore = keyStore;
    }

    public async Task<List<GroupMemberWithRouteDto>> GetGroupMembersWithRoutesAsync(
        ConversationId conversationId,
        int selfIdentityId,
        CancellationToken cancellationToken)
    {
        // Load group members from database
        var members = await _context.GroupMembers
            .Where(m => m.ConversationId == conversationId.Value)
            .OrderBy(m => m.JoinedAtUtc)
            .ToListAsync(cancellationToken);

        var result = new List<GroupMemberWithRouteDto>();

        foreach (var member in members)
        {
            // Convert Guid to Network.PeerId for routing profile lookup
            var networkPeerId = new Percolator.Network.PeerId(member.PeerId);
            
            // Load routing profile for this peer
            var profile = await _routingProfileRepository.GetByIdAsync(networkPeerId, cancellationToken);
            RecipientRoute? deliveryRoute = null;

            if (profile is not null)
            {
                // Determine delivery path using route planner
                var routeSelection = _routePlanner.SelectRoute(profile);
                
                if (routeSelection.Relay is null)
                {
                    // Direct delivery - resolve PKH for this peer
                    var identityPeerId = new Percolator.Identity.PeerId(member.PeerId);
                    var pkh = await _keyStore.GetPublicKeyHashByPeerIdAsync(identityPeerId, cancellationToken);
                    deliveryRoute = new RecipientRoute(identityPeerId, pkh);
                }
                else
                {
                    // Relay delivery via the relay peer
                    var relayPeerId = new Percolator.Identity.PeerId(routeSelection.Relay.RelayPeerId.Value);
                    var relayPkh = await _keyStore.GetPublicKeyHashByPeerIdAsync(relayPeerId, cancellationToken);
                    deliveryRoute = new RecipientRoute(relayPeerId, relayPkh);
                }
            }

            result.Add(new GroupMemberWithRouteDto(
                new Percolator.Identity.PeerId(member.PeerId),
                string.Empty, // DisplayName not available in GroupMemberDbo
                member.Role == Persistence.GroupMemberRole.Admin ? GroupMemberRole.Admin : GroupMemberRole.Member,
                member.JoinedAtUtc,
                member.RemovedAtUtc,
                deliveryRoute));
        }

        return result;
    }
}
