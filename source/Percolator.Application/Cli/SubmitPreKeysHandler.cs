using System.Net;
using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
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
using Percolator.Application.KeyExchange;
using CryptoPeerId = Percolator.Cryptography.Primitives.PeerId;
using IdentityPeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Cli;

public class SubmitPreKeysHandler : IRequestHandler<SubmitPreKeysCommand, int>
{
    private readonly ILogger<SubmitPreKeysHandler> _logger;
    private readonly IDirectSessionLocator _directSessionLocator;
    private readonly IMessageTransportService _transport;
    private readonly ActiveIdentityContext _activeIdentity;
    private readonly IPeerIdentityRepository _peerIdentityRepository;
    private readonly IOneTimeKeyProvider _oneTimeKeyProvider;
    private readonly ISelfPreKeyBundleRepository _selfPreKeyRepo;
    private readonly ISecureMessagingService _secureMessaging;

    public SubmitPreKeysHandler(
        ILogger<SubmitPreKeysHandler> logger,
        IDirectSessionLocator directSessionLocator,
        IMessageTransportService transport,
        ActiveIdentityContext activeIdentity,
        IPeerIdentityRepository peerIdentityRepository,
        IOneTimeKeyProvider oneTimeKeyProvider,
        ISelfPreKeyBundleRepository selfPreKeyRepo,
        ISecureMessagingService secureMessaging)
    {
        _logger = logger;
        _directSessionLocator = directSessionLocator;
        _transport = transport;
        _activeIdentity = activeIdentity;
        _peerIdentityRepository = peerIdentityRepository;
        _oneTimeKeyProvider = oneTimeKeyProvider;
        _selfPreKeyRepo = selfPreKeyRepo;
        _secureMessaging = secureMessaging;
    }

    public async Task<int> Handle(SubmitPreKeysCommand request, CancellationToken cancellationToken)
    {
        if (request.OneTimeKeyCount <= 0)
        {
            throw new ArgumentException("One-time key count must be greater than 0.", nameof(request.OneTimeKeyCount));
        }
        if (_activeIdentity.Identity is null || _activeIdentity.Keys is null)
        {
            throw new InvalidOperationException("Active identity not loaded.");
        }
        
        var identity = await _peerIdentityRepository.GetByNameAsync(new DisplayName(request.TargetPeerName)).ConfigureAwait(false);
        if (identity == null)
        {
            throw new InvalidOperationException("Peer not found.");
        }
        var existingPeer = new Peer(identity.Id, identity.DisplayName?.Value ?? request.TargetPeerName);
        
        // 1) Require existing direct session with target peer (no auto-establish here)
        var existingSessionId = await _directSessionLocator.GetAsync(existingPeer.Id, _activeIdentity.Identity.SelfIdentityId.Value, cancellationToken).ConfigureAwait(false);
        if (existingSessionId is null)
        {
            throw new InvalidOperationException("Direct session not found.");
        }

        // 2) Generate signed pre-key and one-time keys
        var identitySigningSpki = _activeIdentity.Keys.IdentitySigningKey.ExportSubjectPublicKeyInfo();
        using var signedPreKey = _oneTimeKeyProvider.PopOneTimeKey()!;
        var signedPreKeySpki = signedPreKey.ExportSubjectPublicKeyInfo();
        var signedPreKeyPriv = signedPreKey.ExportECPrivateKey();
        var signedPreKeyId = Guid.NewGuid();
        // Sign the signed-pre-key public bytes with identity signing key (ECDSA over SPKI)
        using var ecdsa = ECDsa.Create(_activeIdentity.Keys.IdentitySigningKey.ExportParameters(true));
        var preKeySignature = ecdsa.SignData(signedPreKeySpki, HashAlgorithmName.SHA256);

        var oneTimeList = new List<SubmitPreKeyBundleRequest.Types.OneTimePreKey>(request.OneTimeKeyCount);
        var otkPrivs = new List<(Guid otkId, byte[] otkPriv, byte[] otkSpki)>(request.OneTimeKeyCount);
        for (int i = 0; i < request.OneTimeKeyCount; i++)
        {
            using var otk = _oneTimeKeyProvider.PopOneTimeKey()!;
            var otkSpki = otk.ExportSubjectPublicKeyInfo();
            var otkId = Guid.NewGuid();
            var otkPriv = otk.ExportECPrivateKey();
            otkPrivs.Add((otkId, otkPriv, otkSpki));
            oneTimeList.Add(new SubmitPreKeyBundleRequest.Types.OneTimePreKey
            {
                Version = 1,
                Id = ByteString.CopyFrom(otkId.ToByteArray()),
                PublicKey = ByteString.CopyFrom(otkSpki)
            });
        }

        // 2b) Persist locally for responder use
        var selfId = _activeIdentity.Identity.SelfIdentityId;
        await _selfPreKeyRepo.SaveSignedPreKeyAsync(selfId.Value, signedPreKeyId, signedPreKeyPriv, signedPreKeySpki, preKeySignature, request.ExpiresUtc, cancellationToken).ConfigureAwait(false);
        await _selfPreKeyRepo.SaveOneTimePreKeysAsync(selfId.Value, otkPrivs, cancellationToken).ConfigureAwait(false);

        // 3) Build protobuf request
        var req = new SubmitPreKeyBundleRequest
        {
            Version = 1,
            IdentityKey = ByteString.CopyFrom(identitySigningSpki),
            SignedPreKeyId = ByteString.CopyFrom(signedPreKeyId.ToByteArray()),
            SignedPreKey = ByteString.CopyFrom(signedPreKeySpki),
            PreKeySignature = ByteString.CopyFrom(preKeySignature),
            ExpiresUtc = Timestamp.FromDateTimeOffset(request.ExpiresUtc)
        };
        req.OneTimePreKeys.AddRange(oneTimeList);

        var internalEnvelope = new InternalEnvelope
        {
            PrekeyEnvelope = new PrekeyEnvelope
            {
                Version = 1,
                SubmitPreKeyBundleRequest = req
            }
        };

        // 4) Encrypt and send
        var cryptoPeerId = new CryptoPeerId(existingPeer.Id.Value);
        var deliverResp = await SendAndReceiveAsync(existingSessionId.Value, internalEnvelope, existingPeer.Id, cancellationToken).ConfigureAwait(false);
        
        // 5) Expect empty ack or response payload; if response payload exists, decrypt to check type
        if (deliverResp.ResultCase != DeliverOpaqueMessageResponse.ResultOneofCase.ResponsePayload 
            || deliverResp.ResponsePayload is null 
            || !deliverResp.ResponsePayload.HasResponsePayload)
        {
            throw new InvalidOperationException("No response payload returned.");
        }

        var respCipher = new SessionRatchetMessage(deliverResp.ResponsePayload.ResponsePayload.ToByteArray());
        var resolved = await _secureMessaging.DecryptInboundAsync(respCipher, cancellationToken).ConfigureAwait(false);
        var respPlain = resolved?.plaintext;
        if (respPlain is null)
        {
            _logger.LogWarning("Could not decrypt SubmitPreKeyBundle response payload.");
            return 500;
        }
        var internalResp = InternalEnvelope.Parser.ParseFrom(respPlain.Value);
        if (internalResp.ApplicationPayloadCase != InternalEnvelope.ApplicationPayloadOneofCase.SubmitPreKeyBundleResponse)
        {
            _logger.LogWarning("Unexpected response type: {Type}", internalResp.ApplicationPayloadCase);
            return 500;
        }

        return 0;
    }
    
    private async Task<DeliverOpaqueMessageResponse> SendAndReceiveAsync(
        DirectSessionId directSessionId, 
        InternalEnvelope envelope, 
        IdentityPeerId remotePeerId, 
        CancellationToken cancellationToken)
    {
        var plaintext = new Plaintext(envelope.ToByteArray());
        var ratchetMessage = await _secureMessaging.EncryptAsync(new SessionId(directSessionId.Value), plaintext, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("DHT probe sending (with response) to peer {PeerId}", remotePeerId);
        return await _transport.SendMessageAsync(remotePeerId, directSessionId, ratchetMessage, cancellationToken).ConfigureAwait(false);
    }
}
