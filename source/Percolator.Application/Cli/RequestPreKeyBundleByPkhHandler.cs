using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Application.Network;
using Percolator.Application.Services;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;

namespace Percolator.Application.Cli;

public class RequestPreKeyBundleByPkhHandler : IRequestHandler<RequestPreKeyBundleByPkhCommand, Unit>
{
    private readonly ILogger<RequestPreKeyBundleByPkhHandler> _logger;
    private readonly IDirectSessionLocator _directSessionLocator;
    private readonly ISecureMessagingService _secureMessaging;
    private readonly IMessageTransportService _transport;
    private readonly ActiveIdentityContext _activeIdentity;
    private readonly IPeerPublicSigningKeyStore _peerPublicSigningKeyStore;
    private readonly IOneTimeKeyProvider _oneTimeKeyProvider;

    public RequestPreKeyBundleByPkhHandler(
        ILogger<RequestPreKeyBundleByPkhHandler> logger,
        IDirectSessionLocator directSessionLocator,
        ISecureMessagingService secureMessaging,
        IMessageTransportService transport,
        ActiveIdentityContext activeIdentity,
        IPeerPublicSigningKeyStore peerPublicSigningKeyStore,
        IOneTimeKeyProvider oneTimeKeyProvider)
    {
        _logger = logger;
        _directSessionLocator = directSessionLocator;
        _secureMessaging = secureMessaging;
        _transport = transport;
        _activeIdentity = activeIdentity;
        _peerPublicSigningKeyStore = peerPublicSigningKeyStore;
        _oneTimeKeyProvider = oneTimeKeyProvider;
    }

    public async Task<Unit> Handle(RequestPreKeyBundleByPkhCommand request, CancellationToken cancellationToken)
    {
        if (request.PublicKeyHash is null || request.PublicKeyHash.Length == 0)
        {
            throw new ArgumentException("PublicKeyHash must be provided.", nameof(request.PublicKeyHash));
        }
        var hostPeerId = await _peerPublicSigningKeyStore
            .GetPeerIdByPublicKeyHashAsync(request.PublicKeyHash, cancellationToken)
            .ConfigureAwait(false);
        if (hostPeerId is null)
        {
            throw new InvalidOperationException("Peer not found by PKH.");
        }
        var hostPeer = new Peer(hostPeerId, hostPeerId.Value.ToString());

        if (_activeIdentity.Identity is null)
        {
            throw new InvalidOperationException("Active identity not loaded.");
        }
        var directHostSessionId = await _directSessionLocator.GetAsync(hostPeer.Id, _activeIdentity.Identity.SelfIdentityId.Value, cancellationToken).ConfigureAwait(false);
        if (directHostSessionId is null)
        {
            throw new InvalidOperationException("Direct session not found.");
        }

        var internalEnvelope = new InternalEnvelope
        {
            PrekeyEnvelope = new PrekeyEnvelope
            {
                Version = 1,
                GetPreKeyBundleRequest = new GetPreKeyBundleRequest
                {
                    Version = 1,
                    PublicKeyHash = ByteString.CopyFrom(request.PublicKeyHash)
                }
            }
        };

        // Encrypt and send
        var plaintext = new Plaintext(internalEnvelope.ToByteArray());
        var cryptoHostSessionId = new SessionId(directHostSessionId.Value.Value);
        var ratchetMessage = await _secureMessaging.EncryptAsync(cryptoHostSessionId, plaintext, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Requesting pre-key bundle from peer {PeerId}", hostPeer.Id);
        var deliverResp = await _transport.SendMessageAsync(hostPeer.Id, directHostSessionId.Value, ratchetMessage, cancellationToken).ConfigureAwait(false);

        if (deliverResp.ResultCase != DeliverOpaqueMessageResponse.ResultOneofCase.ResponsePayload
            || deliverResp.ResponsePayload is null
            || !deliverResp.ResponsePayload.HasResponsePayload)
        {
            throw new InvalidOperationException("No response payload returned.");
        }

        var respCipher = new SessionRatchetMessage(deliverResp.ResponsePayload.ResponsePayload.ToByteArray());
        var resolved = await _secureMessaging.DecryptInboundAsync(1, respCipher, cancellationToken).ConfigureAwait(false);
        var respPlain = resolved?.plaintext;
        if (respPlain is null)
        {
            throw new InvalidOperationException("Could not decrypt pre-key bundle response.");
        }

        var internalResp = InternalEnvelope.Parser.ParseFrom(respPlain.Value);
        if (internalResp.ApplicationPayloadCase != InternalEnvelope.ApplicationPayloadOneofCase.GetPreKeyBundleResponse)
        {
            _logger.LogWarning("Unexpected response type: {Type}", internalResp.ApplicationPayloadCase);
            throw new InvalidOperationException("Unexpected response type.");
        }

        if (internalResp.GetPreKeyBundleResponse == null)
        {
            throw new InvalidOperationException("No pre-key bundle returned.");
        }
        
        var preKeyBundle = internalResp.GetPreKeyBundleResponse.PreKeyBundle;

        
        // await PerformHandshake(
        //     new RatchetIdentityKey(preKeyBundle.IdentityKey.ToByteArray()),
        //     new RatchetAgreementKey(preKeyBundle.AgreementKey.ToByteArray()),
        //     new PreKey(preKeyBundle.SignedPreKey.ToByteArray()),
        //         preKeyBundle.HasOneTimeKey ? new OneTimeKey(preKeyBundle.OneTimeKey.ToByteArray()) : null);
        
        return Unit.Value;
    }

    private async Task PerformHandshake(
        RatchetIdentityKey remoteIdentityKey, 
        RatchetEphemeralKey remotePreKey,
        OneTimeKey? remoteOneTimePreKey)
    {
        throw new NotSupportedException("Pre-key handshake cutover pending (Step 8): replace legacy InitiateHandshake/EstablishSession");
    }
}
