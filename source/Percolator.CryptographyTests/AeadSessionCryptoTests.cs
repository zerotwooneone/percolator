using FluentAssertions;
using Percolator.Cryptography;
using System.Security.Cryptography;

namespace Percolator.CryptographyTests;

[TestFixture]
public class AeadSessionCryptoTests
{
    [Test]
    public void EncryptDecrypt_WithSameAssociatedData_Roundtrips()
    {
        var crypto = new AeadSessionCrypto();
        var state = new RatchetState(RootKey.FromBytes(new byte[32]), ChainKey.FromBytes(new byte[32]), 0, ChainKey.FromBytes(new byte[32]), 0, 0, null, null, 1000);
        var pt = Plaintext.FromBytes(new byte[] { 0x10, 0x20 });
        var ad = AssociatedData.FromBytes(new byte[] { 0xAA, 0xBB });
        ulong ctr = 5;
        ulong prevLen = 4;

        var (ct, headerKey, _) = crypto.DR_Encrypt(state, pt, ad, ctr, prevLen);
        var framed = SessionRatchetMessage.Create(headerKey, ctr, prevLen, ct);

        var (round, _) = crypto.DR_Decrypt(state, framed, ad);
        round.ToArray().Should().BeEquivalentTo(pt.ToArray());
    }

    [Test]
    public void Decrypt_WithDifferentAssociatedData_Throws()
    {
        var crypto = new AeadSessionCrypto();
        var state = new RatchetState(RootKey.FromBytes(new byte[32]), ChainKey.FromBytes(new byte[32]), 0, ChainKey.FromBytes(new byte[32]), 0, 0, null, null, 1000);
        var pt = Plaintext.FromBytes(new byte[] { 0x10, 0x20 });
        var ad = AssociatedData.FromBytes(new byte[] { 0xAA, 0xBB });
        var badAd = AssociatedData.FromBytes(new byte[] { 0xCC, 0xDD });
        ulong ctr = 1;
        ulong prevLen = 0;

        var (ct, headerKey, _) = crypto.DR_Encrypt(state, pt, ad, ctr, prevLen);
        var framed = SessionRatchetMessage.Create(headerKey, ctr, prevLen, ct);

        Action act = () => crypto.DR_Decrypt(state, framed, badAd);
        act.Should().Throw<Exception>();
    }

    [Test]
    public void Decrypt_WithEmptyHeaderKey_Throws()
    {
        var crypto = new AeadSessionCrypto();
        var state = new RatchetState(RootKey.FromBytes(new byte[32]), ChainKey.FromBytes(new byte[32]), 0, ChainKey.FromBytes(new byte[32]), 0, 0, null, null, 1000);
        var pt = Plaintext.FromBytes(new byte[] { 0x01 });
        var ad = AssociatedData.None;
        ulong ctr = 0;
        ulong prevLen = 0;

        var (ct, _, _) = crypto.DR_Encrypt(state, pt, ad, ctr, prevLen);
        var badHeader = RatchetEphemeralKey.FromBytes(new byte[64]);
        var framed = SessionRatchetMessage.Create(badHeader, ctr, prevLen, ct);

        Action act = () => crypto.DR_Decrypt(state, framed, ad);
        act.Should().Throw<AuthenticationTagMismatchException>();
    }

    [Test]
    public void Decrypt_WithTamperedCiphertext_Throws()
    {
        var crypto = new AeadSessionCrypto();
        var state = new RatchetState(RootKey.FromBytes(new byte[32]), ChainKey.FromBytes(new byte[32]), 0, ChainKey.FromBytes(new byte[32]), 0, 0, null, null, 1000);
        var pt = Plaintext.FromBytes(new byte[] { 0x42, 0x43, 0x44 });
        var ad = AssociatedData.FromBytes(new byte[] { 0xAA });
        ulong ctr = 2;
        ulong prevLen = 1;

        var (ct, headerKey, _) = crypto.DR_Encrypt(state, pt, ad, ctr, prevLen);
        var tampered = new byte[ct.ToArray().Length];
        Array.Copy(ct.ToArray(), tampered, ct.ToArray().Length);
        tampered[0] ^= 0xFF; // flip one byte
        var framed = SessionRatchetMessage.Create(headerKey, ctr, prevLen, Ciphertext.FromBytes(tampered));

        Action act = () => crypto.DR_Decrypt(state, framed, ad);
        act.Should().Throw<Exception>();
    }
}
