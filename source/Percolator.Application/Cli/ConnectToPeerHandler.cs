using MediatR;
using Percolator.Application.Sessions;
using Percolator.Chat.ValueObjects;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Cli;

public class ConnectToPeerHandler : IRequestHandler<ConnectToPeerCommand, DirectSessionId>
{
    private readonly IConversationService _conversationService;
    private readonly IPeerIdentityRepository _peerIdentityRepository;

    public ConnectToPeerHandler(
        IConversationService conversationService,
        IPeerIdentityRepository peerIdentityRepository)
    {
        _conversationService = conversationService;
        _peerIdentityRepository = peerIdentityRepository;
    }

    public async Task<DirectSessionId> Handle(ConnectToPeerCommand request, CancellationToken cancellationToken)
    {
        var identity = await _peerIdentityRepository.GetByNameAsync(new DisplayName(request.RemotePeerName)).ConfigureAwait(false);
        if (identity == null)
        {
            throw new InvalidOperationException(
                $"Unknown remote peer name '{request.RemotePeerName}'. Provision this peer by SPKI first using SetPeerNameByPublicKeyCommand before connecting.");
        }
        // Bridge: create legacy Peer shape for conversation service until it is refactored
        var remotePeer = new Peer(identity.Id, identity.DisplayName?.Value ?? request.RemotePeerName);
        var existing = await _conversationService.GetExistingDirectSessionAsync(remotePeer).ConfigureAwait(false);
        return existing ?? await _conversationService.CreateNewDirectSessionAsync(request.Endpoint, remotePeer).ConfigureAwait(false);
    }
}
