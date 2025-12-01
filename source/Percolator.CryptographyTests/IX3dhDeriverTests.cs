using System;
using System.Security.Cryptography;
using NUnit.Framework;
using Percolator.Cryptography;

namespace Percolator.CryptographyTests;

[TestFixture]
public class IX3dhDeriverTests
{
    private static (RatchetIdentityKey pub, PrivatePreKey priv) NewIdentityKeyPair()
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var pub = new RatchetIdentityKey(ecdh.PublicKey.ExportSubjectPublicKeyInfo());
        var priv = new PrivatePreKey(ecdh.ExportECPrivateKey());
        return (pub, priv);
    }

    private static (PreKey spkPub, PrivatePreKey spkPriv, Signature spkSig) NewSignedPreKey((RatchetIdentityKey pub, PrivatePreKey priv) id)
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var spkPub = new PreKey(ecdh.PublicKey.ExportSubjectPublicKeyInfo());
        var spkPriv = new PrivatePreKey(ecdh.ExportECPrivateKey());
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportECPrivateKey(id.priv.Value, out _);
        var sig = ecdsa.SignData(spkPub.Value, HashAlgorithmName.SHA256);
        return (spkPub, spkPriv, new Signature(sig));
    }

    private static OneTimeKey NewOneTimePreKey() {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return new OneTimeKey(ecdh.PublicKey.ExportSubjectPublicKeyInfo());
    }

    [Test]
    public void Initiator_and_Responder_match_IRK_and_ephemeral_is_returned()
    {
        var deriver = new X3dhDeriver();
        var (remoteIdPub, remoteIdPriv) = NewIdentityKeyPair();
        var (spkPub, spkPriv, spkSig) = NewSignedPreKey((remoteIdPub, remoteIdPriv));
        var initiatorIdentity = NewIdentityKeyPair();

        // Use no one-time pre-key so both sides derive the same tuples
        OneTimeKey? otk = null;
        var init = deriver.DeriveInitiator(remoteIdPub, spkPub, otk, initiatorIdentity.priv);
        Assert.That(init.InitialRootKey.Value.Length, Is.EqualTo(32));
        Assert.That(init.InitiatorEphemeralPublicKey.Value.Length, Is.GreaterThan(0));

        var resp = deriver.DeriveResponder(initiatorIdentity.pub, init.InitiatorEphemeralPublicKey, remoteIdPriv, spkPriv, null);
        Assert.That(resp.InitialRootKey.Value, Is.EqualTo(init.InitialRootKey.Value));
    }
}
