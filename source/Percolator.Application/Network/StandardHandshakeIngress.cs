using System.Security.Cryptography;
using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Contracts;
using Percolator.Application.KeyExchange;
using Percolator.Application.Services;
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
    private readonly IDirectSessionMappingWriter _directSessionMappingWriter;
    private readonly IClock _clock;
    private readonly IPeerIdentityRepository _peerIdentityRepository;
    private readonly Percolator.Cryptography.ISigningService _signingService;
    private readonly IMediator _mediator;
    private readonly ILogger<StandardHandshakeIngress> _logger;

    public StandardHandshakeIngress(
        ISelfIdentityKeysStore keysStore,
        ISelfPreKeyBundleRepository selfPreKeys,
        ISessionCrypto sessionCrypto,
        ISessionRepository sessions,
        IDirectSessionMappingWriter directSessionMappingWriter,
        IClock clock,
        IPeerIdentityRepository peerIdentityRepository,
        Percolator.Cryptography.ISigningService signingService,
        IMediator mediator,
        ILogger<StandardHandshakeIngress> logger)
    {
        _keysStore = keysStore;
        _selfPreKeys = selfPreKeys;
        _sessionCrypto = sessionCrypto;
        _sessions = sessions;
        _directSessionMappingWriter = directSessionMappingWriter;
        _clock = clock;
        _peerIdentityRepository = peerIdentityRepository;
        _signingService = signingService;
        _mediator = mediator;
        _logger = logger;
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

        var initiatorIdentityPublic = RatchetIdentityKey.FromBytes(initiatorIdentitySpki);
        var initiatorEphemeralPublic = RatchetEphemeralKey.FromBytes(request.EphemeralKey.ToByteArray());

        var localIkPriv = PrivatePreKey.FromBytes(keys.IdentitySigningKey.ExportECPrivateKey());
        var localSpkPriv = PrivatePreKey.FromBytes(spk.Value.spkPrivate);
        var localOtkPriv = otkPriv is null ? null : PrivatePreKey.FromBytes(otkPriv);

        var shared = _sessionCrypto.X3DH_Respond(
            initiatorIdentityPublic,
            initiatorEphemeralPublic,
            localIkPriv,
            localSpkPriv,
            localOtkPriv);

        var root = RootKey.FromBytes(shared.ToArray());
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

        try
        {
            await _directSessionMappingWriter.WriteMappingAsync(
                new Percolator.Network.PeerId(initiatorIdentity.Id.Value),
                new Percolator.Network.DirectSessionId(sessionId.Value),
                selfIdentityId.Value,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex,
                "Failed to persist DirectSession mapping for RemotePeerId={RemotePeerId}, SessionId={SessionId}, SelfIdentityId={SelfIdentityId}",
                initiatorIdentity.Id.Value, sessionId.Value, selfIdentityId.Value);
        }

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
                PayloadSignature = ByteString.CopyFrom(signature.ToArray())
            }
        };
    }
}
