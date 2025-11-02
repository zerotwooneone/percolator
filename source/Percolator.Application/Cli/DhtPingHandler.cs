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

namespace Percolator.Application.Cli;

public sealed class DhtPingHandler : IRequestHandler<DhtPingCommand, Unit>
{
    private readonly ILogger<DhtPingHandler> _logger;
    private readonly IConversationService _conversationService;
    private readonly IMessageService _messageService;
    private readonly IPeerRepository _peerRepository;

    public DhtPingHandler(
        ILogger<DhtPingHandler> logger,
        IConversationService conversationService,
        IMessageService messageService,
        IPeerRepository peerRepository)
    {
        _logger = logger;
        _conversationService = conversationService;
        _messageService = messageService;
        _peerRepository = peerRepository;
    }

    public async Task<Unit> Handle(DhtPingCommand request, CancellationToken cancellationToken)
    {
        var peer = await _peerRepository.GetByNameAsync(request.TargetPeerName).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Peer '{request.TargetPeerName}' not found.");

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
