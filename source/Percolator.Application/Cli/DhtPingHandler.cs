using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging;
using Google.Protobuf;
using Percolator.Application.Sessions;
using Percolator.Application.Network;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;

namespace Percolator.Application.Cli;

public sealed class DhtPingHandler : IRequestHandler<DhtPingCommand, Unit>
{
    private readonly ILogger<DhtPingHandler> _logger;
    private readonly IConversationService _conversationService;
    private readonly IMessageService _messageService;
    private readonly IPeerIdentityRepository _peerIdentityRepository;

    public DhtPingHandler(
        ILogger<DhtPingHandler> logger,
        IConversationService conversationService,
        IMessageService messageService,
        IPeerIdentityRepository peerIdentityRepository)
    {
        _logger = logger;
        _conversationService = conversationService;
        _messageService = messageService;
        _peerIdentityRepository = peerIdentityRepository;
    }

    public async Task<Unit> Handle(DhtPingCommand request, CancellationToken cancellationToken)
    {
        var identity = await _peerIdentityRepository.GetByNameAsync(new DisplayName(request.TargetPeerName)).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Peer '{request.TargetPeerName}' not found.");
        // Bridge to legacy Peer for ConversationService
        var peer = new Peer(identity.Id, identity.DisplayName?.Value ?? request.TargetPeerName);

        var direct = await _conversationService.GetExistingDirectSessionAsync(peer).ConfigureAwait(false)
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
