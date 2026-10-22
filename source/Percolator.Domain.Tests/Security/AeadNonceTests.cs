using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Domain.Tests.Security;

[TestFixture]
public sealed class AeadNonceTests
{
    [Test]
    public void Expand_WithSameMessageKeyAndCounter_ProducesIdenticalCipherKeyAndNonce_DisjointFromSeed()
    {
        var keyBytes = new byte[32];
        keyBytes[0] = 0x42;
        using var key1 = MessageKey.FromSpan(keyBytes);
        using var key2 = MessageKey.FromSpan(keyBytes);

        using var expanded1 = key1.Expand(7);
        using var expanded2 = key2.Expand(7);

        expanded1.CipherKey.Length.Should().Be(AeadNonce.CipherKeyLength);
        expanded1.Nonce.Length.Should().Be(AeadNonce.NonceLength);

        expanded1.CipherKey.SequenceEqual(expanded2.CipherKey).Should().BeTrue();
        expanded1.Nonce.SequenceEqual(expanded2.Nonce).Should().BeTrue();

        // Signal-style HKDF expansion: derived CipherKey is disjoint from the raw MessageKey seed
        expanded1.CipherKey.SequenceEqual(key1.Span).Should().BeFalse();

        // DeriveNonce matches the Nonce slice of Expand
        var derivedNonce = key1.DeriveNonce(7);
        derivedNonce.Span.SequenceEqual(expanded1.Nonce).Should().BeTrue();
    }

    [Test]
    public void Expand_Dispose_ZeroizesMaterial()
    {
        var keyBytes = new byte[32];
        keyBytes[0] = 0x77;
        using var key = MessageKey.FromSpan(keyBytes);

        var expanded = key.Expand(3);
        expanded.IsZeroized.Should().BeFalse();
        expanded.CipherKey.ToArray().Should().Contain(b => b != 0);

        expanded.Dispose();
        expanded.IsZeroized.Should().BeTrue();
        expanded.CipherKey.ToArray().Should().OnlyContain(b => b == 0);
        expanded.Nonce.ToArray().Should().OnlyContain(b => b == 0);
    }

    [Test]
    public void DeriveNonce_WithDifferentCounters_ProducesDistinctNonces()
    {
        var keyBytes = new byte[32];
        keyBytes[0] = 0x42;
        using var key = MessageKey.FromSpan(keyBytes);

        var nonce0 = key.DeriveNonce(0);
        var nonce1 = key.DeriveNonce(1);

        nonce0.Should().NotBe(nonce1);
    }

    [Test]
    public void DeriveNonce_WithDifferentMessageKeys_ProducesDistinctNonces()
    {
        var keyBytesA = new byte[32];
        keyBytesA[0] = 0x01;
        var keyBytesB = new byte[32];
        keyBytesB[0] = 0x02;

        using var keyA = MessageKey.FromSpan(keyBytesA);
        using var keyB = MessageKey.FromSpan(keyBytesB);

        var nonceA = keyA.DeriveNonce(0);
        var nonceB = keyB.DeriveNonce(0);

        nonceA.Should().NotBe(nonceB);
    }

    [Test]
    public void WriteNonce_MatchesDeriveNonce_AndRejectsShortDestination()
    {
        var keyBytes = new byte[32];
        keyBytes[0] = 0x99;
        using var key = MessageKey.FromSpan(keyBytes);

        var derived = key.DeriveNonce(15);
        Span<byte> buffer = stackalloc byte[AeadNonce.NonceLength];
        key.WriteNonce(15, buffer);

        buffer.SequenceEqual(derived.Span).Should().BeTrue();

        var act = () =>
        {
            Span<byte> shortBuf = stackalloc byte[11];
            key.WriteNonce(15, shortBuf);
        };
        act.Should().Throw<ArgumentException>();
    }
}
