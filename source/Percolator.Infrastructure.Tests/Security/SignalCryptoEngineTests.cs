namespace Percolator.Infrastructure.Tests.Security;

using System;
using System.Security.Cryptography;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Security;

[TestFixture]
public sealed class SignalCryptoEngineTests
{
    private readonly SignalCryptoEngine _engine = new();

    [Test]
    public void Ed25519_SignAndVerify_MatchesRfc8032Vectors()
    {
        // RFC 8032 Test Vector 1
        byte[] priv1 = Convert.FromHexString("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");
        byte[] pub1 = Convert.FromHexString("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
        byte[] msg1 = Array.Empty<byte>();
        byte[] expectedSig1 = Convert.FromHexString("e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");

        byte[] sig1 = _engine.SignEd25519(priv1, msg1);
        sig1.Should().Equal(expectedSig1);

        var idKey1 = IdentityKey.FromSpan(pub1);
        _engine.VerifyEd25519Signature(idKey1, msg1, sig1).Should().BeTrue();

        // RFC 8032 Test Vector 2
        byte[] priv2 = Convert.FromHexString("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb");
        byte[] pub2 = Convert.FromHexString("3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c");
        byte[] msg2 = [0x72];
        byte[] expectedSig2 = Convert.FromHexString("92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00");

        byte[] sig2 = _engine.SignEd25519(priv2, msg2);
        sig2.Should().Equal(expectedSig2);

        var idKey2 = IdentityKey.FromSpan(pub2);
        _engine.VerifyEd25519Signature(idKey2, msg2, sig2).Should().BeTrue();

        // Tampered message should fail verification
        _engine.VerifyEd25519Signature(idKey2, [0x73], sig2).Should().BeFalse();
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
