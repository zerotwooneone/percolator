using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.Sessions;
using Percolator.Chat.ValueObjects;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Cli;

public class DhtProbeHandler : IRequestHandler<DhtProbeCommand, FindNodeResponse>
{
    private readonly IConversationService _conversationService;
    private readonly IDirectSessionManager _sessionManager;
    private readonly IMessageTransportService _transport;
    private readonly ActiveIdentityContext _activeIdentityContext;
    private readonly ILogger<DhtProbeHandler> _logger;
    private readonly IPeerRepository _peerRepository;

    public DhtProbeHandler(
        IConversationService conversationService,
        IDirectSessionManager sessionManager,
        IMessageTransportService transport,
        ActiveIdentityContext activeIdentityContext,
        ILogger<DhtProbeHandler> logger,
        IPeerRepository peerRepository)
    {
        _conversationService = conversationService;
        _sessionManager = sessionManager;
        _transport = transport;
        _activeIdentityContext = activeIdentityContext;
        _logger = logger;
        _peerRepository = peerRepository;
    }

    public async Task<FindNodeResponse> Handle(DhtProbeCommand request, CancellationToken cancellationToken)
    {
        var existingPeer = await _peerRepository.GetByNameAsync(request.TargetIdentityName);
        Peer remotePeer;
        if (existingPeer == null)
        {
            remotePeer = new Peer(PeerId.NewId(), request.TargetIdentityName);
            await _peerRepository.AddAsync(remotePeer);
        }
        else
        {
            remotePeer = existingPeer;
        }
        
        // 1) Ensure conversation by connecting (TOFU etc handled by ConversationService)
        var existingDirectConversationAsync = existingPeer == null 
            ? null 
            : await _conversationService.GetExistingDirectConversationAsync(remotePeer);
        var directSessionId = existingDirectConversationAsync ?? await _conversationService.CreateNewDirectSessionAsync(request.Endpoint, remotePeer);

        // 2) Send Ping (fire-and-forget)
        var pingEnvelope = new InternalEnvelope
        {
            DhtEnvelope = new DhtEnvelope { PingRequest = new PingRequest() }
        };
        await SendAndReceiveAsync(directSessionId, pingEnvelope,remotePeer.Id, cancellationToken);


        // 3) Build FindNode with target peer id (use self hashed signing key if unspecified)
        var targetPeerId = GetTargetPeerIdBytes();
        var findNodeEnvelope = new InternalEnvelope
        {
            DhtEnvelope = new DhtEnvelope
            {
                FindNodeRequest = new FindNodeRequest
                {
                    TargetPeerId = ByteString.CopyFrom(targetPeerId)
                }
            }
        };

        // 4) Send and receive response, decrypt and parse
        var response = await SendAndReceiveAsync(directSessionId, findNodeEnvelope, remotePeer.Id, cancellationToken);
        if (!response.HasResponsePayload)
        {
            _logger.LogInformation("No response payload returned for FindNode.");
            return new FindNodeResponse();
        }

        var respRatchet = new SessionRatchetMessage(response.ResponsePayload.ToByteArray());
        var plaintext = await _sessionManager.ReceiveMessageAsync(new SessionId(directSessionId.Value), respRatchet);
        if (plaintext is null)
        {
            _logger.LogWarning("Could not decrypt FindNode response payload.");
            return new FindNodeResponse();
        }

        var internalResp = InternalEnvelope.Parser.ParseFrom(plaintext.Value);
        if (internalResp.ApplicationPayloadCase != InternalEnvelope.ApplicationPayloadOneofCase.DhtEnvelope)
        {
            _logger.LogWarning("Unexpected response envelope type: {Type}", internalResp.ApplicationPayloadCase);
            return new FindNodeResponse();
        }
        var dhtResp = internalResp.DhtEnvelope;
        if (dhtResp.MessageCase != DhtEnvelope.MessageOneofCase.FindNodeResponse)
        {
            _logger.LogWarning("Unexpected DHT response type: {Type}", dhtResp.MessageCase);
            return new FindNodeResponse();
        }
        return dhtResp.FindNodeResponse ?? new FindNodeResponse();

        byte[] GetTargetPeerIdBytes()
        {
            // Prefer hashing our active identity's signing key (SPKI) when available
            if (_activeIdentityContext.Keys?.IdentitySigningKey is null)
            {
                throw new InvalidOperationException("Active identity signing key not loaded.");
            }
            var signingKeySpki = _activeIdentityContext.Keys.IdentitySigningKey.ExportSubjectPublicKeyInfo();
            return System.Security.Cryptography.SHA256.HashData(signingKeySpki);
        }
    }

    private async Task<DeliverOpaqueMessageResponse> SendAndReceiveAsync(
        DirectSessionId directSessionId, 
        InternalEnvelope envelope, 
        PeerId remotePeerId, 
        CancellationToken cancellationToken)
    {
        var plaintext = new Plaintext(envelope.ToByteArray());
        var ratchetMessage = await _sessionManager.EncryptMessageAsync(new SessionId(directSessionId.Value), plaintext);
        _logger.LogInformation("DHT probe sending (with response) to peer {PeerId}", remotePeerId);
        return await _transport.SendMessageAsync(remotePeerId, directSessionId, ratchetMessage, cancellationToken);
    }
}
