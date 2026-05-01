using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.Services;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;

namespace Percolator.Application.Cli;

public class DhtProbeHandler : IRequestHandler<DhtProbeCommand, FindNodeResponse>
{
    private readonly IDirectSessionLocator _directSessionLocator;
    private readonly IHandshakeService _handshake;
    private readonly ISecureMessagingService _secureMessaging;
    private readonly IMessageService _messageService;
    private readonly ActiveIdentityContext _activeIdentityContext;
    private readonly ILogger<DhtProbeHandler> _logger;
    private readonly IPeerIdentityRepository _peerIdentityRepository;
    public DhtProbeHandler(
        IDirectSessionLocator directSessionLocator,
        IHandshakeService handshake,
        ISecureMessagingService secureMessaging,
        IMessageService messageService,
        ActiveIdentityContext activeIdentityContext,
        ILogger<DhtProbeHandler> logger,
        IPeerIdentityRepository peerIdentityRepository)
    {
        _directSessionLocator = directSessionLocator;
        _handshake = handshake;
        _secureMessaging = secureMessaging;
        _messageService = messageService;
        _activeIdentityContext = activeIdentityContext;
        _logger = logger;
        _peerIdentityRepository = peerIdentityRepository;
    }

    public async Task<FindNodeResponse> Handle(DhtProbeCommand request, CancellationToken cancellationToken)
    {
        var identity = await _peerIdentityRepository.GetByNameAsync(new DisplayName(request.TargetIdentityName)).ConfigureAwait(false);
        if (identity is null)
        {
            throw new InvalidOperationException($"Unknown peer name '{request.TargetIdentityName}'. Use SetPeerNameByPublicKeyCommand first.");
        }
        var remotePeer = new Peer(identity.Id, identity.DisplayName?.Value ?? request.TargetIdentityName);
        if (_activeIdentityContext.Identity is null)
        {
            throw new InvalidOperationException("Active identity not loaded.");
        }
        // 1) Ensure direct session (reuse or establish via handshake)
        var directSessionId = await _directSessionLocator.GetAsync(remotePeer.Id, _activeIdentityContext.Identity.SelfIdentityId.Value, cancellationToken).ConfigureAwait(false);
        if (directSessionId is null)
        {
            var cryptoPeerId = new Percolator.Cryptography.Primitives.PeerId(remotePeer.Id.Value);
            var (sessionId, _) = await _handshake.InitiateStandardHandshakeAsync(cryptoPeerId, null, cancellationToken).ConfigureAwait(false);
            directSessionId = new DirectSessionId(sessionId.Value);
        }

        // 2) Send Ping (fire-and-forget)
        var pingEnvelope = new InternalEnvelope
        {
            DhtEnvelope = new DhtEnvelope { PingRequest = new PingRequest() }
        };
        await _messageService.SendMessageAsync(pingEnvelope, remotePeer.Id, cancellationToken).ConfigureAwait(false);


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
        var (sendResult, response) = await _messageService.SendMessageWithResponseAsync(findNodeEnvelope, remotePeer.Id, cancellationToken).ConfigureAwait(false);
        if (response is null
            || response.ResultCase != DeliverOpaqueMessageResponse.ResultOneofCase.ResponsePayload
            || response.ResponsePayload is null
            || !response.ResponsePayload.HasResponsePayload)
        {
            _logger.LogInformation("No response payload returned for FindNode.");
            return new FindNodeResponse();
        }

        var respRatchet = SessionRatchetMessage.FromBytes(response.ResponsePayload.ResponsePayload.ToByteArray());
        var resolved = await _secureMessaging.DecryptInboundAsync(1, respRatchet, cancellationToken).ConfigureAwait(false);
        var plaintext = resolved?.plaintext;
        if (plaintext is null)
        {
            throw new InvalidOperationException("Could not decrypt DHT probe response.");
        }

        var internalResp = InternalEnvelope.Parser.ParseFrom(plaintext.ToArray());
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
}
