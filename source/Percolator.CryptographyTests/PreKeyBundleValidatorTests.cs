using System;
using System.Security.Cryptography;
using NUnit.Framework;
using Percolator.Cryptography;

namespace Percolator.CryptographyTests;

[TestFixture]
public class PreKeyBundleValidatorTests
{
    private static (RatchetIdentityKey pub, PrivatePreKey priv) NewIdentityKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = new RatchetIdentityKey(ecdsa.ExportSubjectPublicKeyInfo());
        var priv = new PrivatePreKey(ecdsa.ExportECPrivateKey());
        return (pub, priv);
    }

    private static (PreKey spkPub, Signature spkSig) NewSignedPreKey((RatchetIdentityKey pub, PrivatePreKey priv) id)
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var spkPub = new PreKey(ecdh.PublicKey.ExportSubjectPublicKeyInfo());
        using var signer = ECDsa.Create();
        signer.ImportECPrivateKey(id.priv.Value, out _);
        var sig = signer.SignData(spkPub.Value, HashAlgorithmName.SHA256);
        return (spkPub, new Signature(sig));
    }

    [Test]
    public void Throws_when_missing_signature()
    {
        var validator = new PreKeyBundleValidator();
        var (idPub, _) = NewIdentityKeyPair();
        // Create a signed pre-key public value but provide an empty signature to simulate missing sig
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var spk = new PreKey(ecdh.PublicKey.ExportSubjectPublicKeyInfo());
        var bundle = new PreKeyBundle(idPub, Guid.NewGuid(), spk, new Signature(Array.Empty<byte>()), null, null, null);
        Assert.Throws<CryptographicException>(() => validator.Validate(bundle));
    }

    [Test]
    public void Throws_when_expired()
    {
        var validator = new PreKeyBundleValidator();
        var (idPub, idPriv) = NewIdentityKeyPair();
        var (spk, sig) = NewSignedPreKey((idPub, idPriv));
        var bundle = new PreKeyBundle(idPub, Guid.NewGuid(), spk, sig, null, null, DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Throws<CryptographicException>(() => validator.Validate(bundle));
    }

    [Test]
    public void Valid_bundle_passes()
    {
        var validator = new PreKeyBundleValidator();
        var (idPub, idPriv) = NewIdentityKeyPair();
        var (spk, sig) = NewSignedPreKey((idPub, idPriv));
        var bundle = new PreKeyBundle(idPub, Guid.NewGuid(), spk, sig, null, null, DateTimeOffset.UtcNow.AddMinutes(10));
        Assert.DoesNotThrow(() => validator.Validate(bundle));
    }
}
