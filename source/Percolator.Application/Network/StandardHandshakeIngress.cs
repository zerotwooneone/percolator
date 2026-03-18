using System.Security.Cryptography;
using Google.Protobuf;
using MediatR;
using Percolator.Contracts;
using Percolator.Application.KeyExchange;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;

namespace Percolator.Application.Network;

internal sealed class StandardHandshakeIngress : IStandardHandshakeIngress
{
    private readonly ISelfIdentityKeysStore _keysStore;
    private readonly ISelfPreKeyBundleRepository _selfPreKeys;
    private readonly ISessionCrypto _sessionCrypto;
    private readonly ISessionRepository _sessions;
    private readonly IDirectSessionRepository _directSessions;
    private readonly IClock _clock;
    private readonly IPeerIdentityRepository _peerIdentityRepository;
    private readonly Percolator.Cryptography.ISigningService _signingService;
    private readonly IMediator _mediator;

    public StandardHandshakeIngress(
        ISelfIdentityKeysStore keysStore,
        ISelfPreKeyBundleRepository selfPreKeys,
        ISessionCrypto sessionCrypto,
        ISessionRepository sessions,
        IDirectSessionRepository directSessions,
        IClock clock,
        IPeerIdentityRepository peerIdentityRepository,
        Percolator.Cryptography.ISigningService signingService,
        IMediator mediator)
    {
        _keysStore = keysStore;
        _selfPreKeys = selfPreKeys;
        _sessionCrypto = sessionCrypto;
        _sessions = sessions;
        _directSessions = directSessions;
        _clock = clock;
        _peerIdentityRepository = peerIdentityRepository;
        _signingService = signingService;
        _mediator = mediator;
    }

    public async Task<EstablishSessionResponse> HandleAsync(SelfId selfIdentityId, EstablishSessionRequest request, CancellationToken ct = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        ct.ThrowIfCancellationRequested();

        var keys = await _keysStore.LoadAsync(selfIdentityId, ct).ConfigureAwait(false);
        if (keys is null)
        {
            throw new InvalidOperationException("Identity keys not loaded.");
        }

        if (!request.HasIdentitySigningKey || request.IdentitySigningKey.Length == 0)
            throw new InvalidOperationException("identity_signing_key is required.");
        if (!request.HasEphemeralKey || request.EphemeralKey.Length == 0)
            throw new InvalidOperationException("ephemeral_key is required.");
        if (!request.HasPrekeyId || request.PrekeyId.Length == 0)
            throw new InvalidOperationException("prekey_id is required.");

        var initiatorIdentitySpki = request.IdentitySigningKey.ToByteArray();
        var initiatorPkh = SHA256.HashData(initiatorIdentitySpki);

        var initiatorIdentity = await _peerIdentityRepository
            .FindByPublicKeyHashAsync(initiatorPkh, ct)
            .ConfigureAwait(false);

        if (initiatorIdentity is null)
        {
            var newId = Percolator.Identity.PeerId.NewId();
            var hex = Convert.ToHexString(initiatorPkh);
            initiatorIdentity = new PeerIdentity(newId);
            initiatorIdentity.SetDisplayName(new DisplayName($"Peer-{hex.Substring(0, Math.Min(12, hex.Length))}"));
            var now = _clock.UtcNow;
            initiatorIdentity.AddKey(initiatorIdentitySpki, notBefore: now, expiresAt: now.AddYears(100), now: now);
            await _peerIdentityRepository.SaveAsync(initiatorIdentity, ct).ConfigureAwait(false);
        }

        Guid signedPreKeyId;
        try
        {
            signedPreKeyId = new Guid(request.PrekeyId.ToByteArray());
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("prekey_id must be a GUID (16 bytes).", ex);
        }

        var spk = await _selfPreKeys.TryGetSignedPreKeyAsync(selfIdentityId.Value, signedPreKeyId, ct).ConfigureAwait(false);
        if (spk is null)
        {
            return new EstablishSessionResponse
            {
                Version = 1,
                NotBefore = new EstablishSessionResponse.Types.NotBefore { Version = 1 }
            };
        }

        byte[]? otkPriv = null;
        if (request.HasOnetimePrekeyId && request.OnetimePrekeyId.Length > 0)
        {
            Guid otkId;
            try
            {
                otkId = new Guid(request.OnetimePrekeyId.ToByteArray());
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("onetime_prekey_id must be a GUID (16 bytes).", ex);
            }

            otkPriv = await _selfPreKeys.TryPopOneTimePreKeyPrivateAsync(selfIdentityId.Value, otkId, ct).ConfigureAwait(false);
        }

        var initiatorIdentityPublic = new RatchetIdentityKey(initiatorIdentitySpki);
        var initiatorEphemeralPublic = new RatchetEphemeralKey(request.EphemeralKey.ToByteArray());

        var localIkPriv = new PrivatePreKey(keys.IdentitySigningKey.ExportECPrivateKey());
        var localSpkPriv = new PrivatePreKey(spk.Value.spkPrivate);
        var localOtkPriv = otkPriv is null ? null : new PrivatePreKey(otkPriv);

        var shared = _sessionCrypto.X3DH_Respond(
            initiatorIdentityPublic,
            initiatorEphemeralPublic,
            localIkPriv,
            localSpkPriv,
            localOtkPriv);

        var root = new RootKey(shared.Value);
        var sessionId = SessionId.NewId();
        var session = RatchetBootstrap.CreateResponderSession(
            sessionId,
            new Percolator.Cryptography.Primitives.PeerId(initiatorIdentity.Id.Value),
            new ProtocolVersion(1),
            root,
            _clock,
            crypto: _sessionCrypto);

        await _sessions.AddAsync(session, ct).ConfigureAwait(false);
        await _mediator.Publish(
                new SecureSessionCreatedNotification(
                    sessionId,
                    SecureSessionCreatedReason.StandardHandshakeIngress,
                    new Percolator.Cryptography.Primitives.PeerId(initiatorIdentity.Id.Value),
                    new ProtocolVersion(1)),
                ct)
            .ConfigureAwait(false);

        await _directSessions.UpsertAsync(
                new Percolator.Network.PeerId(initiatorIdentity.Id.Value),
                new Percolator.Network.DirectSessionId(sessionId.Value),
                selfIdentityId.Value)
            .ConfigureAwait(false);

        var responsePayload = new EstablishSessionResponse.Types.Response.Types.ResponsePayload
        {
            Version = 1,
            EphemeralKey = ByteString.CopyFrom(spk.Value.spkPublicSpki),
            SessionId = sessionId.Value.ToString()
        };

        var responsePayloadBytes = responsePayload.ToByteArray();
        var signature = _signingService.Sign(responsePayloadBytes, keys.IdentitySigningKey);

        //todo: critical: we should not automatically accept the request here. This should be queued for user approval.
        return new EstablishSessionResponse
        {
            Version = 1,
            Response = new EstablishSessionResponse.Types.Response
            {
                Version = 1,
                IdentitySigningKey = ByteString.CopyFrom(keys.IdentitySigningKey.ExportSubjectPublicKeyInfo()),
                ResponsePayload = ByteString.CopyFrom(responsePayloadBytes),
                PayloadSignature = ByteString.CopyFrom(signature.Value)
            }
        };
    }
}
