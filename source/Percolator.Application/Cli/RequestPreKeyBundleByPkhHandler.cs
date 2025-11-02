using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Application.Network;
using Percolator.Application.Sessions;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;
using IdentityPeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Cli;

public class RequestPreKeyBundleByPkhHandler : IRequestHandler<RequestPreKeyBundleByPkhCommand, Unit>
{
    private readonly ILogger<RequestPreKeyBundleByPkhHandler> _logger;
    private readonly IConversationService _conversationService;
    private readonly IDirectSessionManager _sessionManager;
    private readonly IMessageTransportService _transport;
    private readonly ActiveIdentityContext _activeIdentity;
    private readonly IPeerRepository _peerRepository;
    private readonly IDirectSessionManager _directSessionManager;
    private readonly IPeerPublicSigningKeyStore _peerPublicSigningKeyStore;
    private readonly IOneTimeKeyProvider _oneTimeKeyProvider;
    private readonly IX3DHOrchestrator _x3DhOrchestrator;

    public RequestPreKeyBundleByPkhHandler(
        ILogger<RequestPreKeyBundleByPkhHandler> logger,
        IConversationService conversationService,
        IDirectSessionManager sessionManager,
        IMessageTransportService transport,
        ActiveIdentityContext activeIdentity,
        IPeerRepository peerRepository,
        IDirectSessionManager directSessionManager,
        IPeerPublicSigningKeyStore peerPublicSigningKeyStore,
        IOneTimeKeyProvider oneTimeKeyProvider,
        IX3DHOrchestrator x3DHOrchestrator)
    {
        _logger = logger;
        _conversationService = conversationService;
        _sessionManager = sessionManager;
        _transport = transport;
        _activeIdentity = activeIdentity;
        _peerRepository = peerRepository;
        _directSessionManager = directSessionManager;
        _peerPublicSigningKeyStore = peerPublicSigningKeyStore;
        _oneTimeKeyProvider = oneTimeKeyProvider;
        _x3DhOrchestrator = x3DHOrchestrator;
    }

    public async Task<Unit> Handle(RequestPreKeyBundleByPkhCommand request, CancellationToken cancellationToken)
    {
        if (request.PublicKeyHash is null || request.PublicKeyHash.Length == 0)
        {
            throw new ArgumentException("PublicKeyHash must be provided.", nameof(request.PublicKeyHash));
        }
        var existingRemotePeerId = await _peerPublicSigningKeyStore.GetPeerIdByPublicKeyHashAsync(request.PublicKeyHash, cancellationToken).ConfigureAwait(false);
        if (existingRemotePeerId is not null)
        {
            _logger.LogWarning("Peer with public key hash {PublicKeyHash} already exists. Performing handshake anyway...", request.PublicKeyHash);
        }

        var hostPeer = await _peerRepository.GetByNameAsync(request.TargetPeerName).ConfigureAwait(false);
        if (hostPeer is null)
        {
            throw new InvalidOperationException("Peer not found.");
        }

        var directHostSessionId = await _conversationService.GetExistingDirectSessionAsync(hostPeer).ConfigureAwait(false);
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
        var ratchetMessage = await _sessionManager.EncryptMessageAsync(cryptoHostSessionId, plaintext).ConfigureAwait(false);
        _logger.LogInformation("Requesting pre-key bundle from peer {PeerId}", hostPeer.Id);
        var deliverResp = await _transport.SendMessageAsync(hostPeer.Id, directHostSessionId.Value, ratchetMessage, cancellationToken).ConfigureAwait(false);

        if (deliverResp.ResultCase != DeliverOpaqueMessageResponse.ResultOneofCase.ResponsePayload
            || deliverResp.ResponsePayload is null
            || !deliverResp.ResponsePayload.HasResponsePayload)
        {
            throw new InvalidOperationException("No response payload returned.");
        }

        var respCipher = new SessionRatchetMessage(deliverResp.ResponsePayload.ResponsePayload.ToByteArray());
        var respPlain = await _sessionManager.ReceiveMessageAsync(cryptoHostSessionId, respCipher).ConfigureAwait(false);
        if (respPlain is null)
        {
            throw new InvalidOperationException("Could not decrypt GetPreKeyBundle response payload.");
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
        var ephemeralKey = _oneTimeKeyProvider.PopOneTimeKey()!;

        var prekeyBundle = new X3dPreKeyBundle(
            remoteIdentityKey,
            remotePreKey,
            remoteOneTimePreKey);
        
        var sharedSecret = _x3DhOrchestrator.InitiateHandshake(prekeyBundle, ephemeralKey);
        
        var cryptoSessionId = SessionId.NewId();
        await _sessionManager.EstablishSessionAsInitiatorAsync(
            cryptoSessionId,
            remoteIdentityKey,
            remotePreKey,
            sharedSecret,
            ephemeralKey).ConfigureAwait(false);
    }
}
