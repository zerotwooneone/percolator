using MediatR;
using Percolator.Application.Services;
using Percolator.Application.Identity;
using Percolator.Chat.ValueObjects;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Cli;

public class ConnectToPeerHandler : IRequestHandler<ConnectToPeerCommand, DirectSessionId>
{
    private readonly IDirectSessionLocator _directSessionLocator;
    private readonly IHandshakeService _handshake;
    private readonly ActiveIdentityContext _activeIdentityContext;
    private readonly IPeerIdentityRepository _peerIdentityRepository;

    public ConnectToPeerHandler(
        IDirectSessionLocator directSessionLocator,
        IHandshakeService handshake,
        ActiveIdentityContext activeIdentityContext,
        IPeerIdentityRepository peerIdentityRepository)
    {
        _directSessionLocator = directSessionLocator;
        _handshake = handshake;
        _activeIdentityContext = activeIdentityContext;
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
        var remotePeer = new Peer(identity.Id, identity.DisplayName?.Value ?? request.RemotePeerName);
        if (_activeIdentityContext.Identity is null)
        {
            throw new InvalidOperationException("Active identity not loaded.");
        }
        var existing = await _directSessionLocator.GetAsync(remotePeer.Id, _activeIdentityContext.Identity.SelfIdentityId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.Value;
        }
        // Establish via handshake service (initial message not needed here)
        var cryptoPeerId = new Percolator.Cryptography.Primitives.PeerId(remotePeer.Id.Value);
        var (sessionId, _) = await _handshake.InitiateStandardHandshakeAsync(cryptoPeerId, null, cancellationToken).ConfigureAwait(false);
        return new DirectSessionId(sessionId.Value);
    }
}
