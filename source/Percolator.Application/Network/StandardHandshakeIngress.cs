using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Percolator.Contracts;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;

namespace Percolator.Application.Network;

internal sealed class StandardHandshakeIngress : IStandardHandshakeIngress
{
    private readonly IActiveIdentityAccessor _activeIdentityAccessor;
    private readonly ActiveIdentityContext _active;
    private readonly ISelfPreKeyBundleRepository _selfPreKeys;
    private readonly ISessionCrypto _sessionCrypto;
    private readonly ISessionRepository _sessions;
    private readonly IClock _clock;
    private readonly IPeerIdentityRepository _peerIdentityRepository;
    private readonly Percolator.Network.ISigningService _signingService;

    public StandardHandshakeIngress(
        IActiveIdentityAccessor activeIdentityAccessor,
        ActiveIdentityContext active,
        ISelfPreKeyBundleRepository selfPreKeys,
        ISessionCrypto sessionCrypto,
        ISessionRepository sessions,
        IClock clock,
        IPeerIdentityRepository peerIdentityRepository,
        Percolator.Network.ISigningService signingService)
    {
        _activeIdentityAccessor = activeIdentityAccessor;
        _active = active;
        _selfPreKeys = selfPreKeys;
        _sessionCrypto = sessionCrypto;
        _sessions = sessions;
        _clock = clock;
        _peerIdentityRepository = peerIdentityRepository;
        _signingService = signingService;
    }

    public async Task<EstablishSessionResponse> HandleAsync(EstablishSessionRequest request, CancellationToken ct = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        ct.ThrowIfCancellationRequested();

        if (!_activeIdentityAccessor.IsActive || _active.Identity is null || _active.Keys is null)
            throw new InvalidOperationException("Active identity not loaded.");

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

        var selfId = _active.Identity.SelfIdentityId.Value;
        var spk = await _selfPreKeys.TryGetSignedPreKeyAsync(selfId, signedPreKeyId, ct).ConfigureAwait(false);
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

            otkPriv = await _selfPreKeys.TryPopOneTimePreKeyPrivateAsync(selfId, otkId, ct).ConfigureAwait(false);
        }

        var initiatorIdentityPublic = new RatchetIdentityKey(initiatorIdentitySpki);
        var initiatorEphemeralPublic = new RatchetEphemeralKey(request.EphemeralKey.ToByteArray());

        var localIkPriv = new PrivatePreKey(_active.Keys.IdentitySigningKey.ExportECPrivateKey());
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

        var responsePayload = new EstablishSessionResponse.Types.Response.Types.ResponsePayload
        {
            Version = 1,
            EphemeralKey = ByteString.CopyFrom(spk.Value.spkPublicSpki),
            SessionId = sessionId.Value.ToString()
        };

        var responsePayloadBytes = responsePayload.ToByteArray();
        var signature = _signingService.Sign(new Payload(responsePayloadBytes));

        return new EstablishSessionResponse
        {
            Version = 1,
            Response = new EstablishSessionResponse.Types.Response
            {
                Version = 1,
                IdentitySigningKey = ByteString.CopyFrom(_active.Keys.IdentitySigningKey.ExportSubjectPublicKeyInfo()),
                ResponsePayload = ByteString.CopyFrom(responsePayloadBytes),
                PayloadSignature = ByteString.CopyFrom(signature.Value)
            }
        };
    }
}
