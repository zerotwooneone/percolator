using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.KeyExchange;
using Percolator.Application.Services;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Contracts;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;

namespace Percolator.Application.Network.Handshake
{
    internal sealed class InitiatorFinalizeService : IInitiatorFinalizeService
    {
        private readonly ILogger<InitiatorFinalizeService> _logger;
        private readonly ISelfIdentityKeysStore _keysStore;
        private readonly IPreHandshakeSessionStore _prehandshake;
        private readonly ISessionRepository _sessions;
        private readonly IRatchetKeyIndex _index;
        private readonly IClock _clock;
        private readonly ISessionCrypto _sessionCrypto;
        private readonly ISentInvitationRepository _sentInvitations;
        private readonly ISelfPreKeyBundleRepository _selfPreKeys;
        private readonly IPeerIdentityRepository _peerIdentities;
        private readonly IDirectSessionMappingWriter _directSessionMappingWriter;
        private readonly IPeerRoutingProfileRepository _routingProfiles;
        private readonly IMediator _mediator;
        private readonly IEstablishSessionResponseValidator _responseValidator;
        private readonly IPeerRouteCandidateRepository _candidateRepository;
        private readonly IPeerPublicSigningKeyStore _keyStore;

        public InitiatorFinalizeService(
            ILogger<InitiatorFinalizeService> logger,
            ISelfIdentityKeysStore keysStore,
            IPreHandshakeSessionStore prehandshake,
            ISessionRepository sessions,
            IRatchetKeyIndex index,
            IClock clock,
            ISessionCrypto sessionCrypto,
            ISentInvitationRepository sentInvitations,
            ISelfPreKeyBundleRepository selfPreKeys,
            IPeerIdentityRepository peerIdentities,
            IDirectSessionMappingWriter directSessionMappingWriter,
            IPeerRoutingProfileRepository routingProfiles,
            IMediator mediator,
            IEstablishSessionResponseValidator responseValidator,
            IPeerRouteCandidateRepository candidateRepository,
            IPeerPublicSigningKeyStore keyStore)
        {
            _logger = logger;
            _keysStore = keysStore;
            _prehandshake = prehandshake;
            _sessions = sessions;
            _index = index;
            _clock = clock;
            _sessionCrypto = sessionCrypto;
            _sentInvitations = sentInvitations;
            _selfPreKeys = selfPreKeys;
            _peerIdentities = peerIdentities;
            _directSessionMappingWriter = directSessionMappingWriter;
            _routingProfiles = routingProfiles;
            _mediator = mediator;
            _responseValidator = responseValidator;
            _candidateRepository = candidateRepository;
            _keyStore = keyStore;
        }

        public async Task<(SessionId sessionId, Plaintext plaintext)?> TryFinalizeFromInviteHandshakeResponseAsync(
            SelfId selfIdentityId,
            InviteHandshakeResponse response,
            CancellationToken cancellationToken = default)
        {
            if (response is null) throw new ArgumentNullException(nameof(response));

            var keys = await _keysStore.LoadAsync(selfIdentityId, cancellationToken).ConfigureAwait(false);
            if (keys is null)
            {
                throw new InvalidOperationException("Identity keys not loaded.");
            }

            if (!response.HasAcceptorIdentityKey || response.AcceptorIdentityKey.Length == 0)
                throw new InvalidOperationException("acceptor_identity_key is required.");

            if (!response.HasAcceptorX3DhEphemeralKey || response.AcceptorX3DhEphemeralKey.Length == 0)
                throw new InvalidOperationException("acceptor_x3dh_ephemeral_key is required.");

            if (!response.HasInitialRatchetMessage || response.InitialRatchetMessage.Length == 0)
                throw new InvalidOperationException("initial_ratchet_message is required.");

            if (!response.HasAcceptorPublicIdentityId || response.AcceptorPublicIdentityId.Length == 0)
                throw new InvalidOperationException("acceptor_public_identity_id is required.");

            // This is the initiator's view (the inviter who sent the signed pre-key). We derive the shared secret
            // using X3DH_Respond and then decrypt the acceptor's first ratchet message.
            var acceptorIdentityPublic = RatchetIdentityKey.FromBytes(response.AcceptorIdentityKey.ToByteArray());
            var acceptorEphemeralPublic = RatchetEphemeralKey.FromBytes(response.AcceptorX3DhEphemeralKey.ToByteArray());

            // Derive remote PKH from acceptor identity key (SHA-256 of SPKI).
            var remotePkh = System.Security.Cryptography.SHA256.HashData(response.AcceptorIdentityKey.ToByteArray());
            var acceptorPublicIdentityId = new PublicIdentityId(new Guid(response.AcceptorPublicIdentityId.ToByteArray()));

            var localIkPriv = PrivatePreKey.FromBytesOwned(keys.IdentitySigningKey.ExportECPrivateKey());

            // IMPORTANT: For reverse-signal, the inviter's signed pre-key used in the invite may be
            // different from _active.Keys.SignedPreKey. Resolve the correct private key via SentInvitation.
            PrivatePreKey localSpkPriv;
            SentInvitation sentInvitation;
            try
            {
                if (!response.HasRequestCorrelationId
                    || !Guid.TryParse(response.RequestCorrelationId, out var corrGuid)
                    || corrGuid == Guid.Empty)
                {
                    _logger.LogWarning("Invite finalize: missing/invalid request_correlation_id; skipping invite-response finalize.");
                    return null;
                }

                sentInvitation = await _sentInvitations.TryGetAsync(new RequestCorrelationId(corrGuid), new CryptoSelfId(selfIdentityId.Value), cancellationToken).ConfigureAwait(false);
                if (sentInvitation is null)
                {
                    // This commonly happens for peer->main simulator flows (pinv), where main is the acceptor
                    // and therefore never created a matching SentInvitation.
                    _logger.LogInformation("Invite finalize: no sent invitation record found for correlation {CorrelationId}; skipping invite-response finalize.", corrGuid);
                    return null;
                }

                var spk = await _selfPreKeys.TryGetSignedPreKeyAsync(
                        selfIdentityId,
                        sentInvitation.SignedPreKeyId,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (spk is null)
                {
                    _logger.LogWarning("Invite finalize: could not load signed pre-key private for correlation {CorrelationId} (SignedPreKeyId={SignedPreKeyId}); skipping invite-response finalize.", corrGuid, sentInvitation.SignedPreKeyId);
                    return null;
                }

                localSpkPriv = PrivatePreKey.FromBytes(spk.Value.spkPrivate);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Invite finalize: failed to resolve signed pre-key private from sent invitation; skipping invite-response finalize.");
                return null;
            }

            // Resolve or create the peer identity by PublicIdentityId and apply the user-entered display name (if any)
            // from the sent invitation, but do not overwrite an existing user-set name.
            PeerIdentity? peerIdentity = null;
            try
            {
                peerIdentity = await _peerIdentities.GetOrCreateAsync(acceptorPublicIdentityId, cancellationToken).ConfigureAwait(false);

                // Ensure the identity key is registered
                var hasKey = peerIdentity.Keys.Any(k => k.Fingerprint.SequenceEqual(remotePkh));
                if (!hasKey)
                {
                    var now = _clock.UtcNow;
                    peerIdentity.AddKey(response.AcceptorIdentityKey.ToByteArray(), notBefore: now, expiresAt: now.AddYears(100), now: now);
                }

                if (peerIdentity.DisplayName is null && !string.IsNullOrWhiteSpace(sentInvitation.TargetDisplayName))
                {
                    peerIdentity.SetDisplayName(sentInvitation.TargetDisplayName);
                }

                await _peerIdentities.SaveAsync(peerIdentity, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Invite finalize: best-effort peer identity upsert failed.");
                throw;
            }

            SharedSecret shared;
            try
            {
                shared = _sessionCrypto.X3DH_Respond(
                    acceptorIdentityPublic,
                    acceptorEphemeralPublic,
                    localIkPriv,
                    localSpkPriv,
                    localOtkPrivate: null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Invite finalize: X3DH_Respond failed.");
                return null;
            }

            var root = RootKey.FromSpan(shared.Span);

            SessionRatchetMessage ratchetMessage;
            try
            {
                ratchetMessage = SessionRatchetMessage.FromBytesOwned(response.InitialRatchetMessage.ToByteArray());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Invite finalize: initial_ratchet_message was not a valid SessionRatchetMessage.");
                return null;
            }

            (RatchetEphemeralKey PreKey, ulong Counter, ulong PreviousChainLength) header;
            try
            {
                header = ratchetMessage.GetHeader();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Invite finalize: SessionRatchetMessage header parse failed.");
                return null;
            }

            // Acceptor sent first message from an initiator session, so we must bootstrap as responder to decrypt.
            var tmp = RatchetBootstrap.CreateResponderSession(
                SessionId.NewId(),
                new Percolator.Cryptography.Primitives.PeerId(peerIdentity.Id.Value),
                new ProtocolVersion(1),
                root,
                _clock);

            Plaintext pt;
            try
            {
                pt = tmp.Decrypt(ratchetMessage, _clock);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Invite finalize: failed to decrypt initial ratchet message.");
                return null;
            }

            ResponderInnerHello inner;
            try
            {
                inner = ResponderInnerHello.Parser.ParseFrom(pt.ToArray());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Invite finalize: decrypted plaintext was not a valid ResponderInnerHello.");
                return null;
            }

            if (!inner.HasVersion || inner.Version != 1)
            {
                _logger.LogWarning("Invite finalize: ResponderInnerHello version invalid (HasVersion={HasVersion}, Version={Version})", inner.HasVersion, inner.Version);
                return null;
            }
            if (!inner.HasDirectSessionId || string.IsNullOrWhiteSpace(inner.DirectSessionId))
            {
                _logger.LogWarning("Invite finalize: ResponderInnerHello missing direct_session_id.");
                return null;
            }

            SessionId sid;
            try
            {
                sid = new SessionId(Guid.Parse(inner.DirectSessionId));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Invite finalize: direct_session_id was not a GUID: {DirectSessionId}", inner.DirectSessionId);
                return null;
            }

            var final = SecureSession.Create(
                sid,
                tmp.RemotePeerId,
                tmp.ProtocolVersion,
                tmp.State,
                new AeadSessionCrypto(),
                _clock);

            await _sessions.AddAsync(final, new CryptoSelfId(selfIdentityId.Value), cancellationToken).ConfigureAwait(false);

            // Persist mapping between remote peer and session id (used by UI for relay-host selection).
            if (peerIdentity is not null)
            {
                try
                {
                    await _directSessionMappingWriter.WriteMappingAsync(
                        new Percolator.Network.NetworkPeerId(peerIdentity.Id.Value),
                        new Percolator.Network.DirectSessionId(sid.Value),
                        selfIdentityId,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogInformation(ex,
                        "Failed to persist DirectSession mapping for RemotePeerId={RemotePeerId}, SessionId={SessionId}, SelfIdentityId={SelfIdentityId}",
                        peerIdentity.Id.Value, sid.Value, selfIdentityId.Value);
                }

                // Persist routing profile for direct invites so transport can route to this peer.
                // For direct reverse-signal, the inviter had an explicit endpoint at invite time.
                if (sentInvitation.InviteRouteKind == InviteRouteKind.Direct
                    && !string.IsNullOrWhiteSpace(sentInvitation.TargetEndpointHost)
                    && sentInvitation.TargetEndpointPort is not null
                    && sentInvitation.TargetEndpointPort.Value > 0)
                {
                    try
                    {
                        var netPeerId = new Percolator.Network.NetworkPeerId(peerIdentity.Id.Value);
                        var profile = await _routingProfiles.GetByIdAsync(netPeerId, cancellationToken).ConfigureAwait(false)
                            ?? new PeerRoutingProfile();
                        if (profile.Id is null)
                        {
                            profile.BindIdentity(netPeerId);
                        }

                        var endpoint = new System.Net.DnsEndPoint(sentInvitation.TargetEndpointHost!, sentInvitation.TargetEndpointPort.Value);
                        profile.AddGrpcEndPoint(new GrpcEndPoint(endpoint, _clock.UtcNow), _clock.UtcNow);
                        // Bind identity public key from the handshake response (SPKI bytes)
                        profile.SetIdentityPublicKey(Percolator.Network.ValueObjects.IdentityPublicKey.FromBytes(response.AcceptorIdentityKey.ToByteArray()));
                        await _routingProfiles.UpsertAsync(profile, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogInformation(ex, "Invite finalize: best-effort routing profile upsert failed.");
                    }
                }
            }

            await _mediator.Publish(
                    new Percolator.Application.Network.SecureSessionCreatedNotification(
                        sid,
                        Percolator.Application.Network.SecureSessionCreatedReason.InitiatorFinalize),
                    cancellationToken)
                .ConfigureAwait(false);
            await _index.UpsertAsync(new CryptoSelfId(selfIdentityId.Value), sid, header.PreKey, _clock.UtcNow, cancellationToken).ConfigureAwait(false);

            // Once the invite has been finalized into an active session, the outbound pending marker
            // (SentInvitation) should be removed so the UI no longer renders a separate PendingOutbound row.
            try
            {
                await _sentInvitations.DeleteAsync(sentInvitation.RequestCorrelationId, new CryptoSelfId(selfIdentityId.Value), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "Invite finalize: best-effort deletion of sent invitation failed.");
            }

            // Best-effort cleanup of legacy prehandshake store (if it was populated)
            try
            {
                var mostRecent = await _prehandshake.TryGetMostRecentAsync(new NetworkSelfId(selfIdentityId.Value), cancellationToken).ConfigureAwait(false);
                if (mostRecent is not null)
                {
                    await _prehandshake.DeleteAsync(mostRecent.Id, new NetworkSelfId(selfIdentityId.Value), cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                // ignore
            }

            _logger.LogInformation("Initiator finalized session {SessionId} from InviteHandshakeResponse", sid.Value);
            return (sid, pt);
        }

        public async Task<(SessionId sessionId, Plaintext plaintext)?> TryFinalizeFromFirstResponderAsync(
            SelfId selfIdentityId,
            SessionRatchetMessage responderFirst,
            CancellationToken cancellationToken = default)
        {
            // Header's pre-key used to upsert on success
            var header = responderFirst.GetHeader();
            var headerPreKey = header.PreKey;

            await foreach (var pending in _prehandshake.EnumeratePendingAsync(new NetworkSelfId(selfIdentityId.Value), cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var root = RootKey.FromBytesOwned(pending.InitialRootKey);
                    throw new NotImplementedException("we do not have a peer id in this case");
                    var tmp = RatchetBootstrap.CreateInitiatorSession(
                        SessionId.NewId(),
                        new Percolator.Cryptography.Primitives.PeerId(uint.MaxValue),
                        new ProtocolVersion(1),
                        root,
                        _clock);

                    var pt = tmp.Decrypt(responderFirst, _clock);

                    // Parse inner payload for responder-assigned session id
                    var inner = ResponderInnerHello.Parser.ParseFrom(pt.ToArray());
                    if (!inner.HasVersion || inner.Version != 1)
                        throw new InvalidOperationException("Responder inner payload version invalid.");
                    if (!inner.HasDirectSessionId || string.IsNullOrWhiteSpace(inner.DirectSessionId))
                        throw new InvalidOperationException("Responder inner payload missing direct_session_id.");
                    var sid = new SessionId(Guid.Parse(inner.DirectSessionId));

                    // Create final initiator session with progressed state
                    var final = SecureSession.Create(
                        sid,
                        tmp.RemotePeerId,
                        tmp.ProtocolVersion,
                        tmp.State,
                        new AeadSessionCrypto(),
                        _clock);

                    await _sessions.AddAsync(final, new CryptoSelfId(selfIdentityId.Value), cancellationToken).ConfigureAwait(false);

                    // Persist mapping between remote peer and session id (used by UI for relay-host selection).
                    try
                    {
                        await _directSessionMappingWriter.WriteMappingAsync(
                            new Percolator.Network.NetworkPeerId(tmp.RemotePeerId.Value),
                            new Percolator.Network.DirectSessionId(sid.Value),
                            selfIdentityId,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogInformation(ex,
                            "Failed to persist DirectSession mapping for RemotePeerId={RemotePeerId}, SessionId={SessionId}, SelfIdentityId={SelfIdentityId}",
                            tmp.RemotePeerId.Value, sid.Value, selfIdentityId.Value);
                    }

                    await _mediator.Publish(
                            new Percolator.Application.Network.SecureSessionCreatedNotification(
                                sid,
                                Percolator.Application.Network.SecureSessionCreatedReason.InitiatorFinalize),
                            cancellationToken)
                        .ConfigureAwait(false);
                    await _index.UpsertAsync(new CryptoSelfId(selfIdentityId.Value), sid, headerPreKey, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
                    await _prehandshake.DeleteAsync(pending.Id, new NetworkSelfId(selfIdentityId.Value), cancellationToken).ConfigureAwait(false);

                    _logger.LogInformation("Initiator finalized session {SessionId} from pending record {PendingId}", sid.Value, pending.Id);
                    return (sid, pt);
                }
                catch
                {
                    // Try next pending record
                }
            }

            return null;
        }

        public async Task<SessionId?> TryFinalizeFromEstablishSessionResponseAsync(
            SelfId selfIdentityId,
            EstablishSessionResponse response,
            Percolator.Identity.PeerId? relayPeerId = null,
            CancellationToken cancellationToken = default)
        {
            if (response is null) throw new ArgumentNullException(nameof(response));
            cancellationToken.ThrowIfCancellationRequested();

            var validationResult = await _responseValidator.TryValidateAsync(response, cancellationToken).ConfigureAwait(false);
            if (validationResult is null)
            {
                return null;
            }

            var sid = validationResult.SessionId;
            var remoteIdentitySpki = validationResult.RemoteIdentitySpki;
            var remotePkh = validationResult.RemotePublicKeyHash;
            var remotePublicIdentityId = new PublicIdentityId(new Guid(validationResult.RemotePublicIdentityId.ToByteArray()));

            // Match to a pending pre-handshake attempt by recipient PKH
            PreHandshakeRecord? match = null;
            await foreach (var pending in _prehandshake.EnumeratePendingAsync(new NetworkSelfId(selfIdentityId.Value), cancellationToken).ConfigureAwait(false))
            {
                if (pending.RecipientPublicKeyHash is { Length: > 0 }
                    && remotePkh.AsSpan().SequenceEqual(pending.RecipientPublicKeyHash))
                {
                    match = pending;
                    break;
                }
            }

            if (match is null)
            {
                _logger.LogInformation("Standard finalize: no pending record matched responder PKH; skipping");
                return null;
            }

            // Retrieve the SentInvitation to get the user-entered peer name (if any)
            SentInvitation? sentInvitation = null;
            try
            {
                sentInvitation = await _sentInvitations.TryGetAsync(new RequestCorrelationId(match.LocalRequestId), new CryptoSelfId(selfIdentityId.Value), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "Standard finalize: failed to retrieve sent invitation for correlation {CorrelationId}; peer name will not be applied", match.LocalRequestId);
            }

            // Resolve or create peer identity by PublicIdentityId (for stable remote peer id mapping)
            // and apply the user-entered display name (if any) from the sent invitation
            PeerIdentity? peerIdentity = null;
            try
            {
                peerIdentity = await _peerIdentities.GetOrCreateAsync(remotePublicIdentityId, cancellationToken).ConfigureAwait(false);

                // Ensure the identity key is registered
                var hasKey = peerIdentity.Keys.Any(k => k.Fingerprint.SequenceEqual(remotePkh));
                if (!hasKey)
                {
                    var now = _clock.UtcNow;
                    peerIdentity.AddKey(remoteIdentitySpki, notBefore: now, expiresAt: now.AddYears(100), now: now);
                }

                if (sentInvitation is not null && !string.IsNullOrWhiteSpace(sentInvitation.TargetDisplayName))
                {
                    peerIdentity.SetDisplayName(sentInvitation.TargetDisplayName);
                }

                await _peerIdentities.SaveAsync(peerIdentity, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Standard finalize: best-effort peer identity upsert failed;");
                throw;
            }

            var root = RootKey.FromBytesOwned(match.InitialRootKey);
            var remoteCryptoPeerId = new Percolator.Cryptography.Primitives.PeerId(peerIdentity.Id.Value);

            var initiatorSession = RatchetBootstrap.CreateInitiatorSession(
                sid,
                remoteCryptoPeerId,
                new ProtocolVersion(1),
                root,
                _clock,
                crypto: _sessionCrypto);

            await _sessions.AddAsync(initiatorSession, new CryptoSelfId(selfIdentityId.Value), cancellationToken).ConfigureAwait(false);

            if (peerIdentity is not null)
            {
                try
                {
                    await _directSessionMappingWriter.WriteMappingAsync(
                        new Percolator.Network.NetworkPeerId(peerIdentity.Id.Value),
                        new Percolator.Network.DirectSessionId(sid.Value),
                        selfIdentityId,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogInformation(ex,
                        "Failed to persist DirectSession mapping for RemotePeerId={RemotePeerId}, SessionId={SessionId}, SelfIdentityId={SelfIdentityId}",
                        peerIdentity.Id.Value, sid.Value, selfIdentityId.Value);
                }

                // For relayed handshakes, create a routing profile with relay information
                if (relayPeerId is not null)
                {
                    try
                    {
                        var netPeerId = new Percolator.Network.NetworkPeerId(peerIdentity.Id.Value);
                        var profile = await _routingProfiles.GetByIdAsync(netPeerId, cancellationToken).ConfigureAwait(false)
                            ?? new PeerRoutingProfile();
                        if (profile.Id is null)
                        {
                            profile.BindIdentity(netPeerId);
                        }
                        profile.AddOrRefreshRelay(new Percolator.Network.NetworkPeerId(relayPeerId.Value.Value), _clock.UtcNow);
                        profile.SetIdentityPublicKey(Percolator.Network.ValueObjects.IdentityPublicKey.FromBytes(remoteIdentitySpki));
                        await _routingProfiles.UpsertAsync(profile, cancellationToken).ConfigureAwait(false);

                        // Upsert PKH record for target peer so relayed sends can look it up
                        var pkh = IdentityPublicKeyHash.FromBytes(remotePkh);
                        await _keyStore.ActivateIfChangedAsync(peerIdentity.Id, remoteIdentitySpki, pkh, _clock.UtcNow, cancellationToken).ConfigureAwait(false);

                        // Phase 1: Write candidate route for relayed handshake
                        var candidate = new PeerRouteCandidate
                        {
                            SelfIdentityId = selfIdentityId.Value,
                            RemoteNetworkPeerId = netPeerId,
                            RouteKind = RouteKind.Relayed,
                            RelayHostPeerId = new Percolator.Network.NetworkPeerId(relayPeerId.Value.Value),
                            ObservedAtUtc = _clock.UtcNow,
                            AttemptCount = 0,
                            Source = "main-initiated"
                        };
                        await _candidateRepository.UpsertAsync(candidate, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogInformation(ex, "Standard finalize: best-effort routing profile upsert with relay failed.");
                    }
                }
            }

            await _mediator.Publish(
                    new Percolator.Application.Network.SecureSessionCreatedNotification(
                        sid,
                        Percolator.Application.Network.SecureSessionCreatedReason.InitiatorFinalize),
                    cancellationToken)
                .ConfigureAwait(false);

            // Cleanup pending marker and outbound route marker
            try
            {
                await _prehandshake.DeleteAsync(match.Id, new NetworkSelfId(selfIdentityId.Value), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }
            try
            {
                await _sentInvitations.DeleteAsync(new RequestCorrelationId(match.LocalRequestId), new CryptoSelfId(selfIdentityId.Value), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }

            _logger.LogInformation("Initiator finalized session {SessionId} from EstablishSessionResponse", sid.Value);
            return sid;
        }
    }
}
