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
using Percolator.Application.Services;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
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
        byte[]? EncryptedPayload,
        IdentityPeerId? RelayHostPeerId = null) : IRequest<HandleHandshakeInitiatorHelloResult?>;

    public sealed record HandleHandshakeInitiatorHelloResult(IdentityPeerId RemotePeerId, SessionRatchetMessage Cipher);

    internal class HandleHandshakeInitiatorHelloHandler : IRequestHandler<HandleHandshakeInitiatorHelloCommand, HandleHandshakeInitiatorHelloResult?>
    {
        private readonly ILogger<HandleHandshakeInitiatorHelloHandler> _logger;
        private readonly IPeerPublicSigningKeyStore _pkhStore;
        private readonly ISelfPreKeyBundleRepository _selfPreKeyRepo;
        private readonly IDirectSessionRepository _directRepo;
        private readonly ISecureMessagingService _secureMessaging;
        private readonly ActiveIdentityContext _active;
        private readonly IPeerIdentityRepository _peerIdentityRepository;
        private readonly IPeerRoutingProfileRepository _profileRepository;
        private readonly IMediator _mediator;
        public HandleHandshakeInitiatorHelloHandler(
            ILogger<HandleHandshakeInitiatorHelloHandler> logger,
            IPeerPublicSigningKeyStore pkhStore,
            ISelfPreKeyBundleRepository selfPreKeyRepo,
            IDirectSessionRepository directRepo,
            ISecureMessagingService secureMessaging,
            ActiveIdentityContext active,
            IPeerIdentityRepository peerIdentityRepository,
            IPeerRoutingProfileRepository profileRepository,
            IMediator mediator)
        {
            _logger = logger;
            _pkhStore = pkhStore;
            _selfPreKeyRepo = selfPreKeyRepo;
            _directRepo = directRepo;
            _secureMessaging = secureMessaging;
            _active = active;
            _peerIdentityRepository = peerIdentityRepository;
            _profileRepository = profileRepository;
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

            // TODO: Complete responder-side handshake via IHandshakeService (Step 8)
            throw new NotSupportedException("Handshake responder cutover pending (Step 8): replace legacy CompleteHandshake");

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

            // TODO: Establish responder session via domain services
            throw new NotSupportedException("Session establish cutover pending (Step 8): replace legacy EstablishSessionAsResponderAsync");

            // Now it is safe to dispose the temporary one-time ECDH key, if it was used.
            localOneTime?.Dispose();

            _logger.LogInformation("Responder established session {SessionId}", directSessionId.Value);

            // Persist identity artifacts only after successful session establishment
            var displayName = Convert.ToHexString(pkh);
            var identity = await _peerIdentityRepository.GetByIdAsync(resolvedRemotePeerId).ConfigureAwait(false)
                ?? new PeerIdentity(resolvedRemotePeerId);
            identity.SetDisplayName(new DisplayName(displayName));
            await _peerIdentityRepository.SaveAsync(identity).ConfigureAwait(false);
            await _pkhStore.ActivateIfChangedAsync(resolvedRemotePeerId, spki, pkh, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            var netPeerId = new NetworkPeerId(resolvedRemotePeerId.Value);
            var profile = await _profileRepository.GetByIdAsync(netPeerId, cancellationToken).ConfigureAwait(false)
                ?? new PeerRoutingProfile();
            if (profile.Id is null)
            {
                profile.BindIdentity(netPeerId);
            }
            if (request.RelayHostPeerId is not null)
            {
                profile.AddOrRefreshRelay(new NetworkPeerId(request.RelayHostPeerId.Value), DateTimeOffset.UtcNow);
            }
            // Persist initiator identity public key into routing profile
            profile.SetIdentityPublicKey(new Percolator.Network.ValueObjects.IdentityPublicKey(spki));
            await _profileRepository.UpsertAsync(profile, cancellationToken).ConfigureAwait(false);

            // If initiator included an encrypted initial payload, decrypt it via the newly established session
            if (request.EncryptedPayload is not null && request.EncryptedPayload.Length > 0)
            {
                var initPayload = new SessionRatchetMessage(request.EncryptedPayload);
                var resolved = await _secureMessaging.DecryptInboundAsync(initPayload, cancellationToken).ConfigureAwait(false);
                var initPt = resolved?.plaintext;
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
            var rm = await _secureMessaging.EncryptAsync(new SessionId(directSessionId.Value), responderPt, cancellationToken).ConfigureAwait(false);

            return new HandleHandshakeInitiatorHelloResult(resolvedRemotePeerId, rm);
        }
    }
}
