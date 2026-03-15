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
            new RootKey(new byte[32]),
            new ChainKey(new byte[32]), // sending
            0UL,
            new ChainKey(new byte[32]), // receiving
            0UL,
            0UL,
            null,
            null,
            1000);
        var ad = new AssociatedData(new byte[] { 0xAA, 0xBB });
        var pt = new Plaintext(new byte[] { 1, 2, 3, 4 });

        var (ct, header, newState) = engine.Encrypt(state, pt, ad, 0UL, 0UL);
        var framed = SessionRatchetMessage.Create(header, 0UL, 0UL, ct);
        var (pt2, _) = engine.Decrypt(state, framed, ad);

        Assert.That(pt2.Value, Is.EqualTo(pt.Value));
    }

    [Test]
    public void Decrypt_with_tampered_AD_fails()
    {
        var engine = new AeadRatchetEngine();
        var state = new RatchetState(
            new RootKey(new byte[32]),
            new ChainKey(new byte[32]),
            0UL,
            new ChainKey(new byte[32]),
            0UL,
            0UL,
            null,
            null,
            1000);
        var ad = new AssociatedData(new byte[] { 0x10 });
        var pt = new Plaintext(new byte[] { 5, 6 });

        var (ct, header, _) = engine.Encrypt(state, pt, ad, 0UL, 0UL);
        var framed = SessionRatchetMessage.Create(header, 0UL, 0UL, ct);
        var badAd = new AssociatedData(new byte[] { 0x11 });

        Assert.Throws<AuthenticationTagMismatchException>(() => engine.Decrypt(state, framed, badAd));
    }

    [Test]
    public void Decrypt_with_tampered_ciphertext_fails()
    {
        var engine = new AeadRatchetEngine();
        var state = new RatchetState(
            new RootKey(new byte[32]),
            new ChainKey(new byte[32]),
            0UL,
            new ChainKey(new byte[32]),
            0UL,
            0UL,
            null,
            null,
            1000);
        var ad = new AssociatedData(new byte[] { 0x22 });
        var pt = new Plaintext(new byte[] { 9 });

        var (ct, header, _) = engine.Encrypt(state, pt, ad, 0UL, 0UL);
        var tampered = new byte[ct.Value.Length];
        Array.Copy(ct.Value, tampered, ct.Value.Length);
        tampered[^1] ^= 0xFF; // flip last byte
        var framed = SessionRatchetMessage.Create(header, 0UL, 0UL, new Ciphertext(tampered));

        Assert.Throws<AuthenticationTagMismatchException>(() => engine.Decrypt(state, framed, ad));
    }
}
