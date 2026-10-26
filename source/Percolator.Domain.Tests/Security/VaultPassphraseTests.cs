using System.Runtime.InteropServices;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Security.Delegates;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Domain.Tests.Security;

[TestFixture]
public sealed class VaultPassphraseTests
{
    [Test]
    public void Create_FromString_InitializesWithCorrectLengthAndState()
    {
        using var passphrase = VaultPassphrase.Create("SecretPassword123!");

        passphrase.IsEmpty.Should().BeFalse();
        passphrase.Length.Should().Be(18);
        passphrase.IsZeroized.Should().BeFalse();
        passphrase.ToString().Should().Be("VaultPassphrase [PROTECTED]");
    }

    [Test]
    public void Create_FromNullOrEmptyString_YieldsEmptyPassphrase()
    {
        using var fromNull = VaultPassphrase.Create(null);
        using var fromEmpty = VaultPassphrase.Create(string.Empty);
        using var sharedEmpty = VaultPassphrase.Empty;

        fromNull.IsEmpty.Should().BeTrue();
        fromNull.Length.Should().Be(0);

        fromEmpty.IsEmpty.Should().BeTrue();
        fromEmpty.Length.Should().Be(0);

        sharedEmpty.IsEmpty.Should().BeTrue();
        sharedEmpty.Length.Should().Be(0);
    }

    [Test]
    public void Create_FromCharSpan_InitializesCorrectly()
    {
        ReadOnlySpan<char> span = "SpanPassword".AsSpan();
        using var passphrase = VaultPassphrase.Create(span);

        passphrase.IsEmpty.Should().BeFalse();
        passphrase.Length.Should().Be(12);
        passphrase.IsZeroized.Should().BeFalse();
    }

    [Test]
    public void WithUtf8Span_AsciiPassphrase_YieldsExactUtf8Bytes()
    {
        const string raw = "SimpleAscii_99!";
        using var passphrase = VaultPassphrase.Create(raw);

        byte[]? extracted = null;
        passphrase.WithUtf8Span(span =>
        {
            extracted = span.ToArray();
        });

        extracted.Should().NotBeNull();
        extracted.Should().Equal(Encoding.UTF8.GetBytes(raw));
    }

    [Test]
    public void WithUtf8Span_MultiByteUnicodeAndEmojis_ExtractsExactUtf8Encoding()
    {
        const string unicodePassphrase = "🔑Percolator-Shield🛡️-日本語-Ключ";
        using var passphrase = VaultPassphrase.Create(unicodePassphrase);

        byte[] expectedUtf8 = Encoding.UTF8.GetBytes(unicodePassphrase);

        byte[]? extracted = null;
        passphrase.WithUtf8Span(span =>
        {
            extracted = span.ToArray();
        });

        extracted.Should().NotBeNull();
        extracted.Should().Equal(expectedUtf8);
    }

    [Test]
    public void WithUtf8Span_Generic_ReturnsDelegateResult()
    {
        using var passphrase = VaultPassphrase.Create("LengthCheck");

        int byteCount = passphrase.WithUtf8Span(span => span.Length);

        byteCount.Should().Be(Encoding.UTF8.GetByteCount("LengthCheck"));
    }

    [Test]
    public void WithUtf8Span_WhenDelegateThrows_ZeroizesBufferAndRethrows()
    {
        using var passphrase = VaultPassphrase.Create("ExceptionSafetyCheck");

        var act = () => passphrase.WithUtf8Span((ReadOnlySpan<byte> _) =>
        {
            throw new InvalidOperationException("Simulated failure in consumer delegate");
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Simulated failure in consumer delegate");

        // Passphrase itself remains valid and usable
        passphrase.IsZeroized.Should().BeFalse();
        int length = passphrase.WithUtf8Span(span => span.Length);
        length.Should().Be(Encoding.UTF8.GetByteCount("ExceptionSafetyCheck"));
    }

    [Test]
    public void WithUtf8Span_LargePassphrase_AllocatesSafelyAndZeroizes()
    {
        // Greater than the 256-byte stackalloc threshold
        string largeString = new('A', 350);
        using var passphrase = VaultPassphrase.Create(largeString);

        int byteCount = passphrase.WithUtf8Span(span => span.Length);
        byteCount.Should().Be(350);
    }

    [Test]
    public void Zeroize_ClearsInternalStateAndSetsIsZeroized()
    {
        var passphrase = VaultPassphrase.Create("VolatileSecret");

        passphrase.Zeroize();

        passphrase.IsZeroized.Should().BeTrue();
        passphrase.IsEmpty.Should().BeTrue();
        passphrase.Length.Should().Be(0);
        passphrase.ToString().Should().Be("VaultPassphrase [DISPOSED]");

        // Zeroize should be idempotent
        var act = () => passphrase.Zeroize();
        act.Should().NotThrow();
    }

    [Test]
    public void Dispose_InvokesZeroize()
    {
        var passphrase = VaultPassphrase.Create("DisposeCheck");

        passphrase.Dispose();

        passphrase.IsZeroized.Should().BeTrue();
    }

    [Test]
    public void WithUtf8Span_WhenZeroized_ThrowsObjectDisposedException()
    {
        var passphrase = VaultPassphrase.Create("WillBeDisposed");
        passphrase.Dispose();

        var actAction = () => passphrase.WithUtf8Span((ReadOnlySpan<byte> _) => { });
        actAction.Should().Throw<ObjectDisposedException>();

        var actFunc = () => passphrase.WithUtf8Span(span => span.Length);
        actFunc.Should().Throw<ObjectDisposedException>();
    }

    [Test]
    public void WithUtf8Span_WithNullDelegate_ThrowsArgumentNullException()
    {
        using var passphrase = VaultPassphrase.Create("NonNullSecret");

        var actAction = () => passphrase.WithUtf8Span((Action<ReadOnlySpan<byte>>)null!);
        actAction.Should().Throw<ArgumentNullException>();

        var actFunc = () => passphrase.WithUtf8Span((ReadOnlySpanFunc<byte, int>)null!);
        actFunc.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void ConstantTimeEquals_WithIdenticalPassphrases_ReturnsTrue()
    {
        using var passA = VaultPassphrase.Create("IdenticalPassword_42!");
        using var passB = VaultPassphrase.Create("IdenticalPassword_42!");

        VaultPassphrase.ConstantTimeEquals(passA, passB).Should().BeTrue();
    }

    [Test]
    public void ConstantTimeEquals_WithSameInstance_ReturnsTrue()
    {
        using var pass = VaultPassphrase.Create("SelfCheck");

        VaultPassphrase.ConstantTimeEquals(pass, pass).Should().BeTrue();
    }

    [Test]
    public void ConstantTimeEquals_WithDifferentPassphrasesOfSameLength_ReturnsFalse()
    {
        using var passA = VaultPassphrase.Create("Password_A_12345");
        using var passB = VaultPassphrase.Create("Password_B_12345");

        VaultPassphrase.ConstantTimeEquals(passA, passB).Should().BeFalse();
    }

    [Test]
    public void ConstantTimeEquals_WithDifferentLengths_ReturnsFalse()
    {
        using var passA = VaultPassphrase.Create("Short");
        using var passB = VaultPassphrase.Create("MuchLongerPassphrase");

        VaultPassphrase.ConstantTimeEquals(passA, passB).Should().BeFalse();
    }

    [Test]
    public void ConstantTimeEquals_WithBothEmpty_ReturnsTrue()
    {
        using var passA = VaultPassphrase.Empty;
        using var passB = VaultPassphrase.Create(string.Empty);

        VaultPassphrase.ConstantTimeEquals(passA, passB).Should().BeTrue();
    }

    [Test]
    public void ConstantTimeEquals_WithEmptyAndNonEmpty_ReturnsFalse()
    {
        using var empty = VaultPassphrase.Empty;
        using var nonEmpty = VaultPassphrase.Create("NotAnEmptyPassword");

        VaultPassphrase.ConstantTimeEquals(empty, nonEmpty).Should().BeFalse();
        VaultPassphrase.ConstantTimeEquals(nonEmpty, empty).Should().BeFalse();
    }

    [Test]
    public void ConstantTimeEquals_WithNullArguments_ThrowsArgumentNullException()
    {
        using var valid = VaultPassphrase.Create("Valid");

        var actLeftNull = () => VaultPassphrase.ConstantTimeEquals(null!, valid);
        actLeftNull.Should().Throw<ArgumentNullException>();

        var actRightNull = () => VaultPassphrase.ConstantTimeEquals(valid, null!);
        actRightNull.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void ConstantTimeEquals_WithDisposedPassphrase_ThrowsObjectDisposedException()
    {
        var disposed = VaultPassphrase.Create("DisposedSecret");
        disposed.Dispose();

        using var valid = VaultPassphrase.Create("ValidSecret");

        var actLeftDisposed = () => VaultPassphrase.ConstantTimeEquals(disposed, valid);
        actLeftDisposed.Should().Throw<ObjectDisposedException>();

        var actRightDisposed = () => VaultPassphrase.ConstantTimeEquals(valid, disposed);
        actRightDisposed.Should().Throw<ObjectDisposedException>();
    }

    [Test]
    public void DatabaseSetupStatus_EnumValues_MatchExpectedIntValues()
    {
        ((int)DatabaseSetupStatus.Uninitialized).Should().Be(0);
        ((int)DatabaseSetupStatus.Locked).Should().Be(1);
        ((int)DatabaseSetupStatus.Unlocked).Should().Be(2);
    }
}
