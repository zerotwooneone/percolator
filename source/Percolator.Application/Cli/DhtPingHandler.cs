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
    private readonly IDirectSessionManager _sessionManager;
    private readonly IMessageTransportService _transport;
    private readonly IPeerRepository _peerRepository;

    public DhtPingHandler(
        ILogger<DhtPingHandler> logger,
        IConversationService conversationService,
        IDirectSessionManager sessionManager,
        IMessageTransportService transport,
        IPeerRepository peerRepository)
    {
        _logger = logger;
        _conversationService = conversationService;
        _sessionManager = sessionManager;
        _transport = transport;
        _peerRepository = peerRepository;
    }

    public async Task<Unit> Handle(DhtPingCommand request, CancellationToken cancellationToken)
    {
        var peer = await _peerRepository.GetByNameAsync(request.TargetPeerName)
            ?? throw new InvalidOperationException($"Peer '{request.TargetPeerName}' not found.");

        var direct = await _conversationService.GetExistingDirectSessionAsync(peer)
            ?? throw new InvalidOperationException("Direct session not found. Establish a session before DHT ping.");

        var env = new InternalEnvelope
        {
            DhtEnvelope = new DhtEnvelope { PingRequest = new PingRequest() }
        };
        var plaintext = new Plaintext(env.ToByteArray());
        var ratchet = await _sessionManager.EncryptMessageAsync(new SessionId(direct.Value), plaintext);
        _logger.LogInformation("Sending DHT Ping to peer {PeerId}", peer.Id);
        await _transport.SendMessageAsync(peer.Id, direct, ratchet, cancellationToken);
        return Unit.Value;
    }
}
