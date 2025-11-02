using MediatR;
using Percolator.Application.Sessions;
using Percolator.Chat.ValueObjects;
using Percolator.Identity;
using Percolator.Network;
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Cli;

public class ConnectToPeerHandler : IRequestHandler<ConnectToPeerCommand, DirectSessionId>
{
    private readonly IConversationService _conversationService;
    private readonly IPeerRepository _peerRepository;

    public ConnectToPeerHandler(
        IConversationService conversationService,
        IPeerRepository peerRepository)
    {
        _conversationService = conversationService;
        _peerRepository = peerRepository;
    }

    public async Task<DirectSessionId> Handle(ConnectToPeerCommand request, CancellationToken cancellationToken)
    {
        var remotePeer = await _peerRepository.GetByNameAsync(request.RemotePeerName).ConfigureAwait(false);
        if (remotePeer == null)
        {
            throw new InvalidOperationException(
                $"Unknown remote peer name '{request.RemotePeerName}'. Provision this peer by SPKI first using SetPeerNameByPublicKeyCommand before connecting.");
        }
        var existing = await _conversationService.GetExistingDirectSessionAsync(remotePeer).ConfigureAwait(false);
        return existing ?? await _conversationService.CreateNewDirectSessionAsync(request.Endpoint, remotePeer).ConfigureAwait(false);
    }
}
