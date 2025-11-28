using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Network;
using Percolator.Application.Network.Handshake;
using Percolator.Application.Services;
using Percolator.Application.Services;
using Percolator.Contracts;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;
using Percolator.Cryptography;
using Percolator.Application.Identity;

namespace Percolator.Application.Cli;

    public sealed class InitiateHandshakeViaHostHandler : IRequestHandler<InitiateHandshakeViaHostCommand, Unit>
    {
        private readonly ILogger<InitiateHandshakeViaHostHandler> _logger;
        private readonly IDirectSessionLocator _directSessionLocator;
        private readonly ISecureMessagingService _secureMessaging;
        private readonly IMessageTransportService _transport;
        private readonly IPeerIdentityRepository _peerIdentityRepository;
        private readonly IPeerPublicSigningKeyStore _peerPublicSigningKeyStore;
        private readonly IPeerRoutingProfileRepository _profileRepository;
        private readonly Percolator.Application.Network.Handshake.IInitiatorHelloService _initiatorHelloService;
        private readonly ActiveIdentityContext _activeIdentity;

        public InitiateHandshakeViaHostHandler(
            ILogger<InitiateHandshakeViaHostHandler> logger,
            IDirectSessionLocator directSessionLocator,
            ISecureMessagingService secureMessaging,
            IMessageTransportService transport,
            IPeerIdentityRepository peerIdentityRepository,
            IPeerPublicSigningKeyStore peerPublicSigningKeyStore,
            IPeerRoutingProfileRepository profileRepository,
            Percolator.Application.Network.Handshake.IInitiatorHelloService initiatorHelloService,
            ActiveIdentityContext activeIdentity)
        {
            _logger = logger;
            _directSessionLocator = directSessionLocator;
            _secureMessaging = secureMessaging;
            _transport = transport;
            _peerIdentityRepository = peerIdentityRepository;
            _peerPublicSigningKeyStore = peerPublicSigningKeyStore;
            _profileRepository = profileRepository;
            _initiatorHelloService = initiatorHelloService;
            _activeIdentity = activeIdentity;
        }

        public async Task<Unit> Handle(InitiateHandshakeViaHostCommand request, CancellationToken cancellationToken)
        {
            // Resolve Host and ensure an existing direct session to Host
            var hostIdentity = await _peerIdentityRepository.GetByNameAsync(new DisplayName(request.HostPeerName)).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Peer '{request.HostPeerName}' not found.");
            var hostPeer = new Percolator.Identity.Peer(hostIdentity.Id, hostIdentity.DisplayName?.Value ?? request.HostPeerName);
            if (_activeIdentity.Identity is null)
            {
                throw new InvalidOperationException("Active identity not loaded.");
            }
            var hostSession = await _directSessionLocator.GetAsync(hostPeer.Id, _activeIdentity.Identity.SelfIdentityId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Direct session to Host not found. Establish a session before initiating handshake.");

        // Ensure a Peer exists for the target (by PKH) and record a relay connection via Host
        var targetPeerId = await _peerPublicSigningKeyStore.GetPeerIdByPublicKeyHashAsync(request.TargetPublicKeyHash, cancellationToken).ConfigureAwait(false);
        var displayName = request.PeerName ?? Convert.ToHexString(request.TargetPublicKeyHash);
        Percolator.Identity.Peer peer;
        if (targetPeerId is null)
        {
            var identity = new PeerIdentity(Percolator.Identity.PeerId.NewId());
            identity.SetDisplayName(new DisplayName(displayName));
            await _peerIdentityRepository.SaveAsync(identity).ConfigureAwait(false);
            peer = new Percolator.Identity.Peer(identity.Id, identity.DisplayName?.Value ?? displayName);
        }
        else
        {
            var identity = await _peerIdentityRepository.GetByIdAsync(targetPeerId).ConfigureAwait(false) ?? new PeerIdentity(targetPeerId);
            if (identity.DisplayName is null || string.IsNullOrWhiteSpace(identity.DisplayName.Value))
                identity.SetDisplayName(new DisplayName(displayName));
            await _peerIdentityRepository.SaveAsync(identity).ConfigureAwait(false);
            peer = new Percolator.Identity.Peer(identity.Id, identity.DisplayName?.Value ?? displayName);
        }
        // identity already saved above

        // Upsert routing profile with Host recorded as a relay link
        var netPeerId = new Percolator.Network.PeerId(peer.Id.Value);
        var profile = await _profileRepository.GetByIdAsync(netPeerId, cancellationToken).ConfigureAwait(false)
            ?? new PeerRoutingProfile();
        if (profile.Id is null)
        {
            profile.BindIdentity(netPeerId);
        }
        profile.AddOrRefreshRelay(new Percolator.Network.PeerId(hostPeer.Id.Value), DateTimeOffset.UtcNow);
        await _profileRepository.UpsertAsync(profile, cancellationToken).ConfigureAwait(false);

        // 1) Request pre-key bundle for target PKH from Host
        var getReq = new InternalEnvelope
        {
            PrekeyEnvelope = new PrekeyEnvelope
            {
                Version = 1,
                GetPreKeyBundleRequest = new GetPreKeyBundleRequest
                {
                    Version = 1,
                    PublicKeyHash = ByteString.CopyFrom(request.TargetPublicKeyHash)
                }
            }
        };

        var plaintext = new Plaintext(getReq.ToByteArray());
        var cryptoHostSessionId = new SessionId(hostSession.Value);
        var ratchetMessage = await _secureMessaging.EncryptAsync(cryptoHostSessionId, plaintext, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Requesting pre-key bundle for PKH via Host {PeerId}", hostPeer.Id);
        var deliverResp = await _transport.SendMessageAsync(hostPeer.Id, hostSession, ratchetMessage, cancellationToken).ConfigureAwait(false);

        if (deliverResp.ResultCase != DeliverOpaqueMessageResponse.ResultOneofCase.ResponsePayload
            || deliverResp.ResponsePayload is null
            || !deliverResp.ResponsePayload.HasResponsePayload)
        {
            throw new InvalidOperationException("No response payload returned for GetPreKeyBundle.");
        }

        var respCipher = new SessionRatchetMessage(deliverResp.ResponsePayload.ResponsePayload.ToByteArray());
        var resolved = await _secureMessaging.DecryptInboundAsync(respCipher, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Could not decrypt GetPreKeyBundle response payload.");
        var respPlain = resolved.plaintext;

        var internalResp = InternalEnvelope.Parser.ParseFrom(respPlain.Value);
        if (internalResp.ApplicationPayloadCase != InternalEnvelope.ApplicationPayloadOneofCase.GetPreKeyBundleResponse)
            throw new InvalidOperationException("Unexpected response type for GetPreKeyBundle.");
        var bundleMsg = internalResp.GetPreKeyBundleResponse?.PreKeyBundle
            ?? throw new InvalidOperationException("No pre-key bundle found in response.");

        // 2) Orchestrate Initiator Hello enqueue to Host via service
        var remoteIdentitySpki = bundleMsg.IdentityKey?.ToByteArray() ?? Array.Empty<byte>();
        var remoteSignedPreKeySpki = bundleMsg.SignedPreKey?.ToByteArray() ?? Array.Empty<byte>();
        var remotePreKeySignature = bundleMsg.PreKeySignature?.ToByteArray() ?? Array.Empty<byte>();
        if (remoteIdentitySpki.Length == 0 || remoteSignedPreKeySpki.Length == 0 || remotePreKeySignature.Length == 0)
            throw new InvalidOperationException("Incomplete pre-key bundle returned by Host.");

        var signedPreKeyId = bundleMsg.HasSignedPreKeyId
            ? new Guid(bundleMsg.SignedPreKeyId.ToByteArray())
            : throw new InvalidOperationException("SignedPreKeyId missing in bundle.");
        Guid? oneTimePreKeyId = bundleMsg.HasOneTimeKeyId ? new Guid(bundleMsg.OneTimeKeyId.ToByteArray()) : (Guid?)null;
        OneTimeKey? oneTimePreKey = bundleMsg.HasOneTimeKey ? new OneTimeKey(bundleMsg.OneTimeKey.ToByteArray()) : null;

        // Construct full domain PreKeyBundle for planner/crypto
        var remoteBundle = new Percolator.Cryptography.PreKeyBundle(
            identitySigningKey: new RatchetIdentityKey(remoteIdentitySpki),
            signedPreKeyId: signedPreKeyId,
            signedPreKey: new PreKey(remoteSignedPreKeySpki),
            signedPreKeySignature: new Percolator.Cryptography.Signature(remotePreKeySignature),
            oneTimePreKeyId: oneTimePreKeyId,
            oneTimePreKey: oneTimePreKey,
            expirationDateUtc: null);

        // Bind PKH -> PeerId on initiator now that we have the remote SPKI, then upsert routing profile
        var pkh = SHA256.HashData(remoteIdentitySpki);
        await _peerPublicSigningKeyStore.ActivateIfChangedAsync(peer.Id, remoteIdentitySpki, pkh, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        // Name was already persisted in identity repo
        profile.SetIdentityPublicKey(new Percolator.Network.ValueObjects.IdentityPublicKey(remoteIdentitySpki));
        await _profileRepository.UpsertAsync(profile, cancellationToken).ConfigureAwait(false);

        await _initiatorHelloService.SendInitiatorHelloViaHostAsync(
            recipientPublicKeyHash: request.TargetPublicKeyHash,
            remoteBundle: remoteBundle,
            signedPreKeyId: signedPreKeyId,
            oneTimePreKeyId: oneTimePreKeyId,
            hostPeerId: new Percolator.Identity.PeerId(hostPeer.Id.Value),
            initiatorPayload: request.InitiatorPayload,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Initiator Hello enqueued via Host MQ for PKH target.");
        return Unit.Value;
    }
}
