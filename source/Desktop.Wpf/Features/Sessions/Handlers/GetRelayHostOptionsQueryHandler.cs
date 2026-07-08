using Desktop.Wpf.Features.Sessions.Queries;
using MediatR;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;

namespace Desktop.Wpf.Features.Sessions.Handlers;

public sealed class GetRelayHostOptionsQueryHandler : IRequestHandler<GetRelayHostOptionsQuery, GetRelayHostOptionsResult>
{
    private readonly IDirectSessionRepository _directSessions;
    private readonly IPeerIdentityRepository _peerIdentities;

    public GetRelayHostOptionsQueryHandler(
        IDirectSessionRepository directSessions,
        IPeerIdentityRepository peerIdentities)
    {
        _directSessions = directSessions;
        _peerIdentities = peerIdentities;
    }

    public async Task<GetRelayHostOptionsResult> Handle(GetRelayHostOptionsQuery request, CancellationToken cancellationToken)
    {
        IReadOnlyList<DirectSession> sessions;
        try
        {
            sessions = await _directSessions.ListAsync(request.SelfIdentityId).ConfigureAwait(false);
        }
        catch
        {
            sessions = Array.Empty<DirectSession>();
        }

        var options = new List<RelayHostOptionDto>();
        foreach (var s in sessions.OrderBy(x => x.RemoteNetworkPeerId.Value))
        {
            var peerId = new Percolator.Identity.PeerId(s.RemoteNetworkPeerId.Value);

            PeerIdentity? identity;
            try
            {
                identity = await _peerIdentities.GetByIdAsync(peerId, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                identity = null;
            }

            var name = identity?.DisplayName?.Value;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = peerId.Value.ToString()[..8];
            }

            options.Add(new RelayHostOptionDto(new Percolator.Network.NetworkPeerId(peerId.Value), name));
        }

        return new GetRelayHostOptionsResult(options);
    }
}
