using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Application.Sessions;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;
using IdentityPeerId = Percolator.Identity.PeerId;
using NetworkPeerId = Percolator.Network.PeerId;

namespace Percolator.Application.Network.Handshake
{
    // Handles a HandshakeInitiatorHello arriving at this node (acting as responder).
    // Returns responder output including the pre-encrypted ratchet message containing ResponderInnerHello and the resolved RemotePeerId.
    public record HandleHandshakeInitiatorHelloCommand(
        byte[] InitiatorIdentityKeySpki,
        byte[] InitiatorEphemeralKeySpki,
        Guid SignedPreKeyId,
        Guid? OneTimePreKeyId,
        IdentityPeerId? RemotePeerId,
        byte[]? EncryptedPayload) : IRequest<HandleHandshakeInitiatorHelloResult?>;

    public sealed record HandleHandshakeInitiatorHelloResult(IdentityPeerId RemotePeerId, SessionRatchetMessage Cipher);

    internal class HandleHandshakeInitiatorHelloHandler : IRequestHandler<HandleHandshakeInitiatorHelloCommand, HandleHandshakeInitiatorHelloResult?>
    {
        private readonly ILogger<HandleHandshakeInitiatorHelloHandler> _logger;
        private readonly IX3DHOrchestrator _x3dh;
        private readonly IPeerPublicSigningKeyStore _pkhStore;
        private readonly ISelfPreKeyBundleRepository _selfPreKeyRepo;
        private readonly IDirectSessionRepository _directRepo;
        private readonly IDirectSessionManager _sessionManager;
        private readonly ActiveIdentityContext _active;
        private readonly IPeerRepository _peerRepository;
        private readonly IPeerConnectionRepository _peerConnectionRepository;
        private readonly IMediator _mediator;
        public HandleHandshakeInitiatorHelloHandler(
            ILogger<HandleHandshakeInitiatorHelloHandler> logger,
            IX3DHOrchestrator x3dh,
            IPeerPublicSigningKeyStore pkhStore,
            ISelfPreKeyBundleRepository selfPreKeyRepo,
            IDirectSessionRepository directRepo,
            IDirectSessionManager sessionManager,
            ActiveIdentityContext active,
            IPeerRepository peerRepository,
            IPeerConnectionRepository peerConnectionRepository,
            IMediator mediator)
        {
            _logger = logger;
            _x3dh = x3dh;
            _pkhStore = pkhStore;
            _selfPreKeyRepo = selfPreKeyRepo;
            _directRepo = directRepo;
            _sessionManager = sessionManager;
            _active = active;
            _peerRepository = peerRepository;
            _peerConnectionRepository = peerConnectionRepository;
            _mediator = mediator;
        }

        public async Task<HandleHandshakeInitiatorHelloResult?> Handle(HandleHandshakeInitiatorHelloCommand request, CancellationToken cancellationToken)
        {
            var remoteIdentityKey = new RatchetIdentityKey(request.InitiatorIdentityKeySpki);
            var remoteEphemeralKey = new RatchetEphemeralKey(request.InitiatorEphemeralKeySpki);

            // Prepare PKH from initiator identity key; defer persistence until handshake success
            var spki = remoteIdentityKey.Value;
            var pkh = SHA256.HashData(spki);
            var resolvedRemotePeerId = request.RemotePeerId ?? IdentityPeerId.NewId();

            // Pop matching bundle by ids when provided
            var signedPreKeyId = request.SignedPreKeyId;
            Guid? oneTimePreKeyId = request.OneTimePreKeyId;

            // Use our own identity for local self-prekey store (responder owns private pre-keys)
            if (_active.Identity is null)
            {
                throw new InvalidOperationException("Active identity not loaded.");
            }
            // Validate SPK exists locally; consume OTK if referenced
            var spk = await _selfPreKeyRepo.TryGetSignedPreKeyAsync(_active.Identity.SelfIdentityId, signedPreKeyId, cancellationToken).ConfigureAwait(false);
            if (spk is null)
            {
                _logger.LogWarning("Responder SPK not found for id {SpkId}", signedPreKeyId);
                return null;
            }
            ECDiffieHellman? localOneTime = null;
            try
            {
                if (oneTimePreKeyId.HasValue)
                {
                    var otkPriv = await _selfPreKeyRepo.TryPopOneTimePreKeyPrivateAsync(_active.Identity.SelfIdentityId, oneTimePreKeyId.Value, cancellationToken).ConfigureAwait(false);
                    if (otkPriv is null)
                    {
                        _logger.LogWarning("Responder OTK not available for id {OtkId}", oneTimePreKeyId);
                        return null;
                    }
                    localOneTime = ECDiffieHellman.Create();
                    localOneTime.ImportECPrivateKey(otkPriv, out _);
                }
            }
            catch
            {
                localOneTime?.Dispose();
                throw;
            }

            // Complete X3DH (responder). Private OTK retrieval not surfaced here; pass null to use SPK path if needed.
            var hs = _x3dh.CompleteHandshake(remoteIdentityKey, remoteEphemeralKey, localOneTimePreKey: localOneTime);
            localOneTime?.Dispose();

            // Upsert or create direct session mapping
            DirectSessionId directSessionId;
            if (request.RemotePeerId is not null)
            {
                var existing = await _directRepo.GetByRemotePeerIdAsync(new NetworkPeerId(request.RemotePeerId.Value), _active.Identity.SelfIdentityId).ConfigureAwait(false);
                directSessionId = existing?.SessionId ?? new DirectSessionId(Guid.NewGuid());
                if (existing is null)
                {
                    await _directRepo.UpsertAsync(new NetworkPeerId(request.RemotePeerId.Value), directSessionId, _active.Identity.SelfIdentityId).ConfigureAwait(false);
                }
            }
            else
            {
                directSessionId = new DirectSessionId(Guid.NewGuid());
            }

            await _sessionManager.EstablishSessionAsResponderAsync(
                new SessionId(directSessionId.Value),
                remoteIdentityKey,
                new RatchetEphemeralKey(remoteEphemeralKey.Value),
                hs.ResponderPrivateKeyUsed,
                hs.SharedSecret).ConfigureAwait(false);

            _logger.LogInformation("Responder established session {SessionId}", directSessionId.Value);

            // Persist identity artifacts only after successful session establishment
            var displayName = Convert.ToHexString(pkh);
            await _peerRepository.AddOrUpdateAsync(new Peer(resolvedRemotePeerId, displayName)).ConfigureAwait(false);
            await _pkhStore.ActivateIfChangedAsync(resolvedRemotePeerId, spki, pkh, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            var netPeerId = new NetworkPeerId(resolvedRemotePeerId.Value);
            var existingConn = await _peerConnectionRepository.GetByIdAsync(netPeerId).ConfigureAwait(false);
            if (existingConn is null)
            {
                var conn = new PeerConnection(netPeerId, identitySigningKey: null, grpcEndPoints: Array.Empty<GrpcEndPoint>(), tlsCertificates: Array.Empty<TlsCertificate>(), lastSeen: DateTimeOffset.UtcNow);
                await _peerConnectionRepository.SaveAsync(conn).ConfigureAwait(false);
            }

            // If initiator included an encrypted initial payload, decrypt it via the newly established session
            if (request.EncryptedPayload is not null && request.EncryptedPayload.Length > 0)
            {
                var initPayload = new SessionRatchetMessage(request.EncryptedPayload);
                var initPt = await _sessionManager.ReceiveMessageAsync(new SessionId(directSessionId.Value), initPayload).ConfigureAwait(false);
                if (initPt is not null)
                {
                    var initInner = InternalEnvelope.Parser.ParseFrom(initPt.Value);
                    // Optional: common logging/context step
                    var ctx = new Percolator.Application.Network.SessionContext(directSessionId.Value, _active.Identity.SelfIdentityId, request.RemotePeerId?.Value);
                    await _mediator.Send(new Percolator.Application.Network.ProcessInternalEnvelopeCommand(initInner, ctx), cancellationToken).ConfigureAwait(false);
                }
            }

            // Build inner responder payload and encrypt it over the newly established session
            var responderInner = new ResponderInnerHello
            {
                Version = 1,
                DirectSessionId = directSessionId.Value.ToString()
            };
            var responderPt = new Plaintext(responderInner.ToByteArray());
            var rm = await _sessionManager.EncryptMessageAsync(new SessionId(directSessionId.Value), responderPt).ConfigureAwait(false);

            return new HandleHandshakeInitiatorHelloResult(resolvedRemotePeerId, rm);
        }
    }
}
