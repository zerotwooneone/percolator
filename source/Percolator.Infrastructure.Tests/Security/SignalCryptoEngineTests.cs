namespace Percolator.Infrastructure.Tests.Security;

using System;
using System.Security.Cryptography;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Security;
using Signal.Interop;

[TestFixture]
public sealed class SignalCryptoEngineTests
{
    private readonly SignalCryptoEngine _engine = new(new CryptographyOptions());

    [Test]
    public void Ed25519_GenerateSignAndVerify_SucceedsAndRejectsTamperedMessage()
    {
        byte[] priv = new byte[32];
        byte[] pub = new byte[32];
        SignalCrypto.GenerateEd25519KeyPair(priv, pub);

        byte[] msg = "Percolator Identity Assertion Payload"u8.ToArray();
        byte[] sig = _engine.SignEd25519(priv, msg);
        sig.Length.Should().Be(64);

        var idKey = IdentityKey.FromSpan(pub);
        _engine.VerifyEd25519Signature(idKey, msg, sig).Should().BeTrue();

        // Tampered message should fail verification
        byte[] tamperedMsg = "Percolator Identity Assertion Tampered"u8.ToArray();
        _engine.VerifyEd25519Signature(idKey, tamperedMsg, sig).Should().BeFalse();

        // Corrupted signature should fail verification
        byte[] corruptedSig = (byte[])sig.Clone();
        corruptedSig[0] ^= 0xFF;
        _engine.VerifyEd25519Signature(idKey, msg, corruptedSig).Should().BeFalse();
    }

    [Test]
    public void Curve25519_DiffieHellman_MatchesRfc7748Vectors()
    {
        byte[] alicePriv = Convert.FromHexString("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
        byte[] alicePub = Convert.FromHexString("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a");
        byte[] bobPriv = Convert.FromHexString("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");
        byte[] bobPub = Convert.FromHexString("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f");
        byte[] expectedShared = Convert.FromHexString("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742");

        var sharedAlice = _engine.ComputeDiffieHellman(alicePriv, bobPub);
        var sharedBob = _engine.ComputeDiffieHellman(bobPriv, alicePub);

        sharedAlice.Span.ToArray().Should().Equal(expectedShared);
        sharedBob.Span.ToArray().Should().Equal(expectedShared);
    }

    [Test]
    public void AesGcm_EncryptDecrypt_SucceedsAndRejectsBitflip()
    {
        byte[] key = new byte[32];
        RandomNumberGenerator.Fill(key);
        byte[] nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);
        byte[] plaintext = "Hello Secure Percolator Protocol Wire!"u8.ToArray();
        byte[] aad = "Additional Authenticated Data"u8.ToArray();

        byte[] encrypted = _engine.EncryptAesGcm(key, nonce, plaintext, aad);
        encrypted.Length.Should().Be(plaintext.Length + 16);

        byte[] decrypted = _engine.DecryptAesGcm(key, nonce, encrypted, aad);
        decrypted.Should().Equal(plaintext);

        // Bitflip should fail authentication tag check
        byte[] corrupted = (byte[])encrypted.Clone();
        corrupted[0] ^= 0x01;

        Action act = () => _engine.DecryptAesGcm(key, nonce, corrupted, aad);
        act.Should().Throw<AuthenticationTagMismatchException>();
    }
}
