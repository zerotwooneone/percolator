using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.Sessions;
using Percolator.Application.Services;
using Percolator.Chat.ValueObjects;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Cli;

public class DhtProbeHandler : IRequestHandler<DhtProbeCommand, FindNodeResponse>
{
    private readonly IConversationService _conversationService;
    private readonly ISecureMessagingService _secureMessaging;
    private readonly IMessageService _messageService;
    private readonly ActiveIdentityContext _activeIdentityContext;
    private readonly ILogger<DhtProbeHandler> _logger;
    private readonly IPeerIdentityRepository _peerIdentityRepository;
    public DhtProbeHandler(
        IConversationService conversationService,
        ISecureMessagingService secureMessaging,
        IMessageService messageService,
        ActiveIdentityContext activeIdentityContext,
        ILogger<DhtProbeHandler> logger,
        IPeerIdentityRepository peerIdentityRepository)
    {
        _conversationService = conversationService;
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
        
        // 1) Ensure conversation by connecting (TOFU etc handled by ConversationService)
        var existingDirectConversationAsync = await _conversationService.GetExistingDirectSessionAsync(remotePeer).ConfigureAwait(false);
        var directSessionId = existingDirectConversationAsync ?? await _conversationService.CreateNewDirectSessionAsync(request.Endpoint, remotePeer).ConfigureAwait(false);

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

        var respRatchet = new SessionRatchetMessage(response.ResponsePayload.ResponsePayload.ToByteArray());
        var resolved = await _secureMessaging.DecryptInboundAsync(respRatchet, cancellationToken).ConfigureAwait(false);
        var plaintext = resolved?.plaintext;
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
}
