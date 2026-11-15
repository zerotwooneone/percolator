using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Tests.Identities;

[TestFixture]
public sealed class SignedPreKeyIdTests
{
    [Test]
    public void Sentinels_ReflectValidityInvariant()
    {
        SignedPreKeyId.First.Value.Should().Be(1u);
        SignedPreKeyId.First.IsValid.Should().BeTrue();

        SignedPreKeyId.None.Value.Should().Be(0u);
        SignedPreKeyId.None.IsValid.Should().BeFalse();

        new SignedPreKeyId(0).IsValid.Should().BeFalse();
        new SignedPreKeyId(25).IsValid.Should().BeTrue();
    }

    [TestCase(1u, "1")]
    [TestCase(42u, "42")]
    public void ToString_ReturnsFormattedValue(uint rawValue, string expected)
    {
        new SignedPreKeyId(rawValue).ToString().Should().Be(expected);
    }
}
