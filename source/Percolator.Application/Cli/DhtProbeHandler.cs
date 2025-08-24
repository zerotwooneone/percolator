using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.Sessions;
using Percolator.Chat.ValueObjects;
using Percolator.Contracts;
using Percolator.Cryptography;

namespace Percolator.Application.Cli;

public class DhtProbeHandler : IRequestHandler<DhtProbeCommand, FindNodeResponse>
{
    private readonly IConversationService _conversationService;
    private readonly IDirectSessionManager _sessionManager;
    private readonly IMessageTransportService _transport;
    private readonly ActiveIdentityContext _activeIdentityContext;
    private readonly ILogger<DhtProbeHandler> _logger;

    public DhtProbeHandler(
        IConversationService conversationService,
        IDirectSessionManager sessionManager,
        IMessageTransportService transport,
        ActiveIdentityContext activeIdentityContext,
        ILogger<DhtProbeHandler> logger)
    {
        _conversationService = conversationService;
        _sessionManager = sessionManager;
        _transport = transport;
        _activeIdentityContext = activeIdentityContext;
        _logger = logger;
    }

    public async Task<FindNodeResponse> Handle(DhtProbeCommand request, CancellationToken cancellationToken)
    {
        // 1) Ensure conversation by connecting (TOFU etc handled by ConversationService)
        var conversationId = await _conversationService.CreateDirectConversationAsync(request.Endpoint, request.TargetIdentityName);

        // 2) Send Ping (fire-and-forget)
        var pingEnvelope = new InternalEnvelope
        {
            DhtEnvelope = new DhtEnvelope { PingRequest = new PingRequest() }
        };
        await SendFireAndForgetAsync(conversationId, pingEnvelope, cancellationToken);

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
        var response = await SendAndReceiveAsync(conversationId, findNodeEnvelope, cancellationToken);
        if (!response.HasResponsePayload)
        {
            _logger.LogInformation("No response payload returned for FindNode.");
            return new FindNodeResponse();
        }

        var respRatchet = new SessionRatchetMessage(response.ResponsePayload.ToByteArray());
        var plaintext = await _sessionManager.ReceiveMessageAsync(new SessionId(conversationId.Value), respRatchet);
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

    private async Task SendFireAndForgetAsync(ConversationId conversationId, InternalEnvelope envelope, CancellationToken cancellationToken)
    {
        var plaintext = new Plaintext(envelope.ToByteArray());
        var (remotePeerId, ratchetMessage) = await _sessionManager.EncryptMessageAsync(new SessionId(conversationId.Value), plaintext);
        _logger.LogInformation("DHT probe sending (fire-and-forget) to peer {PeerId}", remotePeerId);
        await _transport.SendMessageAsync(new Percolator.Identity.PeerId(remotePeerId.Value), conversationId, ratchetMessage);
    }

    private async Task<DeliverOpaqueMessageResponse> SendAndReceiveAsync(ConversationId conversationId, InternalEnvelope envelope, CancellationToken cancellationToken)
    {
        var plaintext = new Plaintext(envelope.ToByteArray());
        var (remotePeerId, ratchetMessage) = await _sessionManager.EncryptMessageAsync(new SessionId(conversationId.Value), plaintext);
        _logger.LogInformation("DHT probe sending (with response) to peer {PeerId}", remotePeerId);
        return await _transport.SendMessageAsync(new Percolator.Identity.PeerId(remotePeerId.Value), conversationId, ratchetMessage, cancellationToken);
    }
}
