using System.Security.Cryptography;
using FluentAssertions;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.CryptographyTests;

file sealed class TestClock_X3dhRoundtrip : IClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2025-07-02T00:00:00Z");
}

[TestFixture]
public class SecureSessionX3dhRoundtripTests
{
    private static (RatchetIdentityKey pub, PrivatePreKey priv) NewIdentityKeyPair()
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var pub = RatchetIdentityKey.FromBytes(ecdh.PublicKey.ExportSubjectPublicKeyInfo());
        var priv = PrivatePreKey.FromBytes(ecdh.ExportECPrivateKey());
        return (pub, priv);
    }

    private static (PreKey spkPub, PrivatePreKey spkPriv, Signature spkSig) NewSignedPreKey((RatchetIdentityKey pub, PrivatePreKey priv) id)
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var spkPub = PreKey.FromBytes(ecdh.PublicKey.ExportSubjectPublicKeyInfo());
        var spkPriv = PrivatePreKey.FromBytes(ecdh.ExportECPrivateKey());

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportECPrivateKey(id.priv.ToArray(), out _);
        var sig = ecdsa.SignData(spkPub.ToArray(), HashAlgorithmName.SHA256);
        return (spkPub, spkPriv, Signature.FromBytes(sig));
    }

    [Test]
    public void Establish_and_roundtrip_messages_in_both_directions_using_real_crypto()
    {
        var clock = new TestClock_X3dhRoundtrip();
        var crypto = new AeadSessionCrypto();

        // Responder publishes bundle (IK_B + SPK_B + Sig(SPK_B))
        var responderId = NewIdentityKeyPair();
        var (responderSpkPub, responderSpkPriv, responderSpkSig) = NewSignedPreKey(responderId);
        var bundle = new PreKeyBundle(
            responderId.pub,
            signedPreKeyId: Guid.NewGuid(),
            signedPreKey: responderSpkPub,
            signedPreKeySignature: responderSpkSig,
            oneTimePreKeyId: null,
            oneTimePreKey: null,
            expirationDateUtc: null);

        // Initiator uses its IK_A private and responder bundle to derive shared secret + EK_A pub
        var initiatorId = NewIdentityKeyPair();
        var (sharedA, ephA) = crypto.X3DH_Initiate(initiatorId.priv, bundle);

        // Responder derives the same shared secret using its private material
        var sharedB = crypto.X3DH_Respond(initiatorId.pub, ephA, responderId.priv, responderSpkPriv, null);
        sharedB.ToArray().Should().Equal(sharedA.ToArray());

        // Use the shared secret as ratchet root and create complementary states
        var root = RootKey.FromBytes(sharedA.ToArray());
        var (initiatorState, responderState) = CryptoTestBootstrap.CreatePairedStates(root);

        var a = SecureSession.Create(SessionId.NewId(), PeerId.NewId(), new ProtocolVersion(1), initiatorState, crypto, clock);
        var b = SecureSession.Create(SessionId.NewId(), PeerId.NewId(), new ProtocolVersion(1), responderState, crypto, clock);

        // A -> B
        var msgA0 = a.Encrypt(Plaintext.FromBytes(new byte[] { 0x09 }), clock);
        var ptA0 = b.Decrypt(msgA0, clock);
        ptA0.ToArray().Should().Equal(new byte[] { 0x09 });

        // B -> A
        var msgB0 = b.Encrypt(Plaintext.FromBytes(new byte[] { 0x01, 0x02, 0x03 }), clock);
        var ptB0 = a.Decrypt(msgB0, clock);
        ptB0.ToArray().Should().Equal(new byte[] { 0x01, 0x02, 0x03 });
    }
}
