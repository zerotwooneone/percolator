using System.Security.Cryptography;
using Desktop.Wpf.Features.Simulator.Models;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Desktop.Wpf.Features.Simulator.Protocol;

public sealed class SignalProtocolEngine : ISignalProtocolEngine
{
    private readonly ISessionCrypto _crypto;
    private readonly IClock _clock;

    public SignalProtocolEngine(IClock clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _crypto = new AeadSessionCrypto();
    }

    public PreKeyBundleForPublish CreateStandardPreKeyBundle(
        SimulatedPeerModel peer,
        DateTimeOffset? expiresUtc,
        int oneTimeKeyCount)
    {
        if (peer is null) throw new ArgumentNullException(nameof(peer));

        var signedPreKey = EnsureSignedPreKey(peer);

        using var identityEcdh = ECDiffieHellman.Create();
        identityEcdh.ImportECPrivateKey(peer.IdentitySigningKeyPrivateKeyEcPrivateKey, out _);
        using var identityEcdsa = ECDsa.Create(identityEcdh.ExportParameters(true));
        var signature = identityEcdsa.SignData(signedPreKey.PublicSpki, HashAlgorithmName.SHA256);

        var oneTimeKeys = oneTimeKeyCount == 0 ? Array.Empty<OneTimeKeyInstance>() : new OneTimeKeyInstance[oneTimeKeyCount];
        for (int oneTimeIndex = 0; oneTimeIndex < oneTimeKeyCount; oneTimeIndex++)
        {
            using var identityEcdh2 = ECDiffieHellman.Create();
            identityEcdh2.ImportECPrivateKey(peer.IdentitySigningKeyPrivateKeyEcPrivateKey, out _);
            var curve = identityEcdh2.ExportParameters(false).Curve;
            using var otk = ECDiffieHellman.Create(curve);
            var otkId = Guid.NewGuid();
            var otkPublicSpki = otk.PublicKey.ExportSubjectPublicKeyInfo();
            var otkPrivateBytes = otk.ExportECPrivateKey();

            // Store private key on the peer's private OTK store
            var otkPrivateRecord = new SimulatedOneTimePreKeyPrivateRecord(
                SimulatedOneTimePreKeyId.FromGuid(otkId),
                PrivatePreKey.FromBytes(otkPrivateBytes),
                _clock.UtcNow);
            peer.OneTimePreKeysPrivateMutable.Add(otkPrivateRecord);

            // Publish only the public portion
            oneTimeKeys[oneTimeIndex] = new OneTimeKeyInstance(otkId, OneTimeKey.FromBytes(otkPublicSpki));
        }
        
        return new PreKeyBundleForPublish(
            identitySigningKey: RatchetIdentityKey.FromBytes(peer.IdentitySigningKeySpki),
            signedPreKeyId: signedPreKey.SignedPreKeyId,
            signedPreKey: PreKey.FromBytes(signedPreKey.PublicSpki),
            signedPreKeySignature: Signature.FromBytes(signature),
            oneTimeKeys: oneTimeKeys,
            expirationDateUtc: expiresUtc);
    }

    public InitiateStandardHandshakeResult? TryInitiateStandardHandshake(
        SimulatedPeerModel initiator,
        PreKeyBundle responderBundle)
    {
        if (initiator is null) throw new ArgumentNullException(nameof(initiator));
        if (responderBundle is null) throw new ArgumentNullException(nameof(responderBundle));

        if (!_crypto.VerifySignature(responderBundle.IdentitySigningKey, responderBundle.SignedPreKey, responderBundle.SignedPreKeySignature))
        {
            return null;
        }

        var localIkPriv = PrivatePreKey.FromBytes(initiator.IdentitySigningKeyPrivateKeyEcPrivateKey);
        var x3 = _crypto.X3DH_Initiate(localIkPriv, responderBundle);

        var sessionId = SessionId.NewId();
        var root = RootKey.FromSpan(x3.SharedSecret.Span);

        var session = RatchetBootstrap.CreateInitiatorSession(
            sessionId,
            new PeerId((uint)Random.Shared.Next()),
            new ProtocolVersion(1),
            root,
            _clock,
            crypto: _crypto);

        initiator.SessionsMutable[sessionId] = session;

        return new InitiateStandardHandshakeResult(
            SessionId: sessionId,
            InitiatorIdentitySigningKeySpki: initiator.IdentitySigningKeySpki,
            InitiatorEphemeralKeySpki: x3.EphemeralPublic.ToArray(),
            SignedPreKeyId: responderBundle.SignedPreKeyId,
            OneTimePreKeyId: responderBundle.OneTimePreKeyId,
            InitialRootKey: x3.SharedSecret.ToArray());
    }

    public SessionRatchetMessage Encrypt(
        SimulatedPeerModel sender,
        SessionId sessionId,
        Plaintext plaintext)
    {
        if (sender is null) throw new ArgumentNullException(nameof(sender));
        if (plaintext is null) throw new ArgumentNullException(nameof(plaintext));

        if (!sender.SessionsMutable.TryGetValue(sessionId, out var session))
        {
            throw new InvalidOperationException($"No session exists for simulated peer {sender.NetworkPeerId} with id {sessionId.Value}");
        }

        var cipher = session.Encrypt(plaintext, _clock);
        sender.SessionsMutable[sessionId] = session;
        return cipher;
    }

    public Plaintext Decrypt(
        SimulatedPeerModel receiver,
        SessionId sessionId,
        SessionRatchetMessage message)
    {
        if (receiver is null) throw new ArgumentNullException(nameof(receiver));
        if (message is null) throw new ArgumentNullException(nameof(message));

        if (!receiver.SessionsMutable.TryGetValue(sessionId, out var session))
        {
            throw new InvalidOperationException($"No session exists for simulated peer {receiver.NetworkPeerId} with id {sessionId.Value}");
        }

        var pt = session.Decrypt(message, _clock);
        receiver.SessionsMutable[sessionId] = session;
        return pt;
    }

    public AcceptReverseSignalInviteResult AcceptReverseSignalInvite(
        SimulatedPeerModel acceptor,
        ReverseSignalInviteDomain invite,
        Guid inviterPeerId)
        => throw new NotSupportedException("Reverse-signal invite flow not migrated to SignalProtocolEngine yet.");

    public FinalizeInviteHandshakeResponseResult? TryFinalizeInviteHandshakeResponse(
        SimulatedPeerModel inviter,
        InviteHandshakeResponseDomain response,
        Guid acceptorPeerId,
        Guid requestCorrelationId)
        => throw new NotSupportedException("Invite handshake response finalize not migrated to SignalProtocolEngine yet.");

    private static SimulatedSignedPreKeyModel EnsureSignedPreKey(SimulatedPeerModel peer)
    {
        if (peer.SignedPreKeysMutable.Count > 0)
        {
            return peer.SignedPreKeysMutable[0];
        }

        using var identityEcdh = ECDiffieHellman.Create();
        identityEcdh.ImportECPrivateKey(peer.IdentitySigningKeyPrivateKeyEcPrivateKey, out _);
        var curve = identityEcdh.ExportParameters(false).Curve;

        using var spk = ECDiffieHellman.Create(curve);
        var spkId = Guid.NewGuid();
        var spkSpki = spk.PublicKey.ExportSubjectPublicKeyInfo();
        var spkPriv = spk.ExportECPrivateKey();

        var model = new SimulatedSignedPreKeyModel(spkId, spkPriv, spkSpki);
        peer.SignedPreKeysMutable.Add(model);
        return model;
    }
}
