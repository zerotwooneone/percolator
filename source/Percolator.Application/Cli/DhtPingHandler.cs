using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging;
using Google.Protobuf;
using Percolator.Application.Network;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Application.Services;
using Percolator.Application.Identity;

namespace Percolator.Application.Cli;

public sealed class DhtPingHandler : IRequestHandler<DhtPingCommand, Unit>
{
    private readonly ILogger<DhtPingHandler> _logger;
    private readonly IDirectSessionLocator _directSessionLocator;
    private readonly IMessageService _messageService;
    private readonly IPeerIdentityRepository _peerIdentityRepository;
    private readonly ActiveIdentityContext _activeIdentity;

    public DhtPingHandler(
        ILogger<DhtPingHandler> logger,
        IDirectSessionLocator directSessionLocator,
        IMessageService messageService,
        IPeerIdentityRepository peerIdentityRepository,
        ActiveIdentityContext activeIdentity)
    {
        _logger = logger;
        _directSessionLocator = directSessionLocator;
        _messageService = messageService;
        _peerIdentityRepository = peerIdentityRepository;
        _activeIdentity = activeIdentity;
    }

    public async Task<Unit> Handle(DhtPingCommand request, CancellationToken cancellationToken)
    {
        var identity = await _peerIdentityRepository.GetByNameAsync(new DisplayName(request.TargetPeerName)).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Peer '{request.TargetPeerName}' not found.");
        // Bridge to legacy Peer for ConversationService
        var peer = new Peer(identity.Id, identity.DisplayName?.Value ?? request.TargetPeerName);
        if (_activeIdentity.Identity is null)
        {
            throw new InvalidOperationException("Active identity not loaded.");
        }
        var direct = await _directSessionLocator.GetAsync(peer.Id, _activeIdentity.Identity.SelfIdentityId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Direct session not found. Establish a session before DHT ping.");

        var env = new InternalEnvelope
        {
            DhtEnvelope = new DhtEnvelope { PingRequest = new PingRequest() }
        };
        _logger.LogInformation("Sending DHT Ping to peer {PeerId}", peer.Id);
        await _messageService.SendMessageAsync(env, peer.Id, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
