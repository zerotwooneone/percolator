using System.Security.Cryptography;
using Percolator.Cryptography;

namespace Percolator.CryptographyTests;

[TestFixture]
public class IRatchetEngineTests
{
    [Test]
    public void Encrypt_Decrypt_roundtrip_with_same_AD_succeeds()
    {
        var engine = new AeadRatchetEngine();
        var state = new RatchetState(
            RootKey.FromBytes(new byte[32]),
            ChainKey.FromBytes(new byte[32]), // sending
            0UL,
            ChainKey.FromBytes(new byte[32]), // receiving
            0UL,
            0UL,
            null,
            null,
            1000);
        var ad = AssociatedData.FromBytes(new byte[] { 0xAA, 0xBB });
        var pt = Plaintext.FromBytes(new byte[] { 1, 2, 3, 4 });

        var (ct, header, newState) = engine.Encrypt(state, pt, ad, 0UL, 0UL);
        var framed = SessionRatchetMessage.Create(header, 0UL, 0UL, ct);
        var (pt2, _) = engine.Decrypt(state, framed, ad);

        Assert.That(pt2.ToArray(), Is.EqualTo(pt.ToArray()));
    }

    [Test]
    public void Decrypt_with_tampered_AD_fails()
    {
        var engine = new AeadRatchetEngine();
        var state = new RatchetState(
            RootKey.FromBytes(new byte[32]),
            ChainKey.FromBytes(new byte[32]),
            0UL,
            ChainKey.FromBytes(new byte[32]),
            0UL,
            0UL,
            null,
            null,
            1000);
        var ad = AssociatedData.FromBytes(new byte[] { 0x10 });
        var pt = Plaintext.FromBytes(new byte[] { 5, 6 });

        var (ct, header, _) = engine.Encrypt(state, pt, ad, 0UL, 0UL);
        var framed = SessionRatchetMessage.Create(header, 0UL, 0UL, ct);
        var badAd = AssociatedData.FromBytes(new byte[] { 0x11 });

        Assert.Throws<AuthenticationTagMismatchException>(() => engine.Decrypt(state, framed, badAd));
    }

    [Test]
    public void Decrypt_with_tampered_ciphertext_fails()
    {
        var engine = new AeadRatchetEngine();
        var state = new RatchetState(
            RootKey.FromBytes(new byte[32]),
            ChainKey.FromBytes(new byte[32]),
            0UL,
            ChainKey.FromBytes(new byte[32]),
            0UL,
            0UL,
            null,
            null,
            1000);
        var ad = AssociatedData.FromBytes(new byte[] { 0x22 });
        var pt = Plaintext.FromBytes(new byte[] { 9 });

        var (ct, header, _) = engine.Encrypt(state, pt, ad, 0UL, 0UL);
        var tampered = new byte[ct.ToArray().Length];
        Array.Copy(ct.ToArray(), tampered, ct.ToArray().Length);
        tampered[^1] ^= 0xFF; // flip last byte
        var framed = SessionRatchetMessage.Create(header, 0UL, 0UL, Ciphertext.FromBytes(tampered));

        Assert.Throws<AuthenticationTagMismatchException>(() => engine.Decrypt(state, framed, ad));
    }
}
