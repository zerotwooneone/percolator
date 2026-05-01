using System.Security.Cryptography;
using Percolator.Cryptography;

namespace Percolator.CryptographyTests;

[TestFixture]
public class PreKeyBundleValidatorTests
{
    private static (RatchetIdentityKey pub, PrivatePreKey priv) NewIdentityKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = RatchetIdentityKey.FromBytes(ecdsa.ExportSubjectPublicKeyInfo());
        var priv = PrivatePreKey.FromBytes(ecdsa.ExportECPrivateKey());
        return (pub, priv);
    }

    private static (PreKey spkPub, Signature spkSig) NewSignedPreKey((RatchetIdentityKey pub, PrivatePreKey priv) id)
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var spkPub = PreKey.FromBytes(ecdh.PublicKey.ExportSubjectPublicKeyInfo());
        using var signer = ECDsa.Create();
        signer.ImportECPrivateKey(id.priv.ToArray(), out _);
        var sig = signer.SignData(spkPub.ToArray(), HashAlgorithmName.SHA256);
        return (spkPub, Signature.FromBytes(sig));
    }

    [Test]
    public void Throws_when_missing_signature()
    {
        var validator = new PreKeyBundleValidator();
        var (idPub, _) = NewIdentityKeyPair();
        // Create a signed pre-key public value but provide an invalid signature to simulate missing sig
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var spk = PreKey.FromBytes(ecdh.PublicKey.ExportSubjectPublicKeyInfo());
        var bundle = new PreKeyBundle(idPub, Guid.NewGuid(), spk, Signature.FromBytes(new byte[60]), null, null, null);
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
