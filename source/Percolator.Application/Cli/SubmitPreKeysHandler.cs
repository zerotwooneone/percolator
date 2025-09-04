using System.Net;
using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.Sessions;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;
using CryptoPeerId = Percolator.Cryptography.Primitives.PeerId;
using IdentityPeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Cli;

public class SubmitPreKeysHandler : IRequestHandler<SubmitPreKeysCommand, int>
{
    private readonly ILogger<SubmitPreKeysHandler> _logger;
    private readonly IConversationService _conversationService;
    private readonly IDirectSessionManager _sessionManager;
    private readonly IMessageTransportService _transport;
    private readonly ActiveIdentityContext _activeIdentity;
    private readonly IPeerRepository _peerRepository;
    private readonly IOneTimeKeyProvider _oneTimeKeyProvider;

    public SubmitPreKeysHandler(
        ILogger<SubmitPreKeysHandler> logger,
        IConversationService conversationService,
        IDirectSessionManager sessionManager,
        IMessageTransportService transport,
        ActiveIdentityContext activeIdentity,
        IPeerRepository peerRepository,
        IOneTimeKeyProvider oneTimeKeyProvider)
    {
        _logger = logger;
        _conversationService = conversationService;
        _sessionManager = sessionManager;
        _transport = transport;
        _activeIdentity = activeIdentity;
        _peerRepository = peerRepository;
        _oneTimeKeyProvider = oneTimeKeyProvider;
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
        
        var existingPeer = await _peerRepository.GetByNameAsync(request.TargetPeerName);
        if (existingPeer == null)
        {
            throw new InvalidOperationException("Peer not found.");
        }
        
        // 1) Ensure/establish direct session with target peer
        var existingSessionId = await _conversationService.GetExistingDirectSessionAsync(existingPeer);
        if (existingSessionId is null)
        {
            throw new InvalidOperationException("Direct session not found.");
        }

        // 2) Generate signed pre-key and one-time keys
        var identitySigningSpki = _activeIdentity.Keys.IdentitySigningKey.ExportSubjectPublicKeyInfo();
        using var signedPreKey = _oneTimeKeyProvider.PopOneTimeKey()!;
        var signedPreKeySpki = signedPreKey.ExportSubjectPublicKeyInfo();
        var signedPreKeyId = Guid.NewGuid();
        // Sign the signed-pre-key public bytes with identity signing key (ECDSA over SPKI)
        using var ecdsa = ECDsa.Create(_activeIdentity.Keys.IdentitySigningKey.ExportParameters(true));
        var preKeySignature = ecdsa.SignData(signedPreKeySpki, HashAlgorithmName.SHA256);

        var oneTimeList = new List<SubmitPreKeyBundleRequest.Types.OneTimePreKey>(request.OneTimeKeyCount);
        for (int i = 0; i < request.OneTimeKeyCount; i++)
        {
            using var otk = _oneTimeKeyProvider.PopOneTimeKey()!;
            var otkSpki = otk.ExportSubjectPublicKeyInfo();
            var otkId = Guid.NewGuid();
            oneTimeList.Add(new SubmitPreKeyBundleRequest.Types.OneTimePreKey
            {
                Version = 1,
                Id = ByteString.CopyFrom(otkId.ToByteArray()),
                PublicKey = ByteString.CopyFrom(otkSpki)
            });
        }

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
        var deliverResp = await SendAndReceiveAsync(existingSessionId.Value, internalEnvelope, existingPeer.Id, cancellationToken);
        
        // 5) Expect empty ack or response payload; if response payload exists, decrypt to check type
        if (!deliverResp.HasResponsePayload)
        {
            throw new InvalidOperationException("No response payload returned.");
        }
        
        var respCipher = new SessionRatchetMessage(deliverResp.ResponsePayload.ToByteArray());
        var respPlain = await _sessionManager.ReceiveMessageAsync(new SessionId(existingSessionId.Value.Value), respCipher);
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
        var ratchetMessage = await _sessionManager.EncryptMessageAsync(new SessionId(directSessionId.Value), plaintext);
        _logger.LogInformation("DHT probe sending (with response) to peer {PeerId}", remotePeerId);
        return await _transport.SendMessageAsync(remotePeerId, directSessionId, ratchetMessage, cancellationToken);
    }
}
