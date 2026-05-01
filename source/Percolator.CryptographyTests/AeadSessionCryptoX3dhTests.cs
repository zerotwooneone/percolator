using System.Security.Cryptography;
using FluentAssertions;
using Percolator.Cryptography;

namespace Percolator.CryptographyTests;

[TestFixture]
public class AeadSessionCryptoX3dhTests
{
    [Test]
    public void X3DH_Initiate_WithInvalidSignature_Throws()
    {
        var crypto = new AeadSessionCrypto();
        // Identity signing key (public) as RatchetIdentityKey (SPKI bytes)
        using var idPub = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var identitySpki = idPub.ExportSubjectPublicKeyInfo();
        var identity = RatchetIdentityKey.FromBytes(identitySpki);
        // Signed pre-key public (SPKI) - using ECDH for DH import path
        using var spk = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var spkPub = PreKey.FromBytes(spk.PublicKey.ExportSubjectPublicKeyInfo());
        // Signature is invalid (random bytes)
        var signature = Signature.FromBytes(RandomNumberGenerator.GetBytes(64));
        // Bundle without one-time key
        var bundle = new PreKeyBundle(identity,
            signedPreKeyId: Guid.NewGuid(),
            signedPreKey: spkPub,
            signedPreKeySignature: signature,
            oneTimePreKeyId: null,
            oneTimePreKey: null,
            expirationDateUtc: null);
        // Local identity private key for initiator
        using var ikA = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var localPriv = PrivatePreKey.FromBytes(ikA.ExportECPrivateKey());

        Action act = () => crypto.X3DH_Initiate(localPriv, bundle);
        act.Should().Throw<CryptographicException>();
    }
}
