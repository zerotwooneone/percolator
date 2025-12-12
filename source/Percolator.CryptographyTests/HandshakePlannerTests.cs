using System;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Percolator.Cryptography;

namespace Percolator.CryptographyTests;

[TestFixture]
public class HandshakePlannerTests
{
    [Test]
    public void ValidateInvitation_Throws_When_Empty()
    {
        var planner = new HandshakePlanner();
        var inv = new HandshakeInvitation(Array.Empty<byte>());
        Assert.Throws<ArgumentException>(() => planner.ValidateInvitation(inv, new Mock<ICryptoPrimitives>().Object));
    }

    [Test]
    public void PlanEstablishment_Reflects_OneTimePreKey_Presence()
    {
        var planner = new HandshakePlanner();
        var idKey = new RatchetIdentityKey(new byte[] { 1 });
        var pre = new PreKey(new byte[] { 2 });
        var sig = new Signature(new byte[] { 3 });
        var bundleWithOtp = new PreKeyBundle(idKey, Guid.NewGuid(), pre, sig, Guid.NewGuid(), new OneTimeKey(new byte[] { 4 }), DateTimeOffset.UtcNow);
        var plan1 = planner.PlanEstablishment(bundleWithOtp);
        plan1.HasOneTimePreKey.Should().BeTrue();

        var bundleWithoutOtp = new PreKeyBundle(idKey, Guid.NewGuid(), pre, sig, null, null, null);
        var plan2 = planner.PlanEstablishment(bundleWithoutOtp);
        plan2.HasOneTimePreKey.Should().BeFalse();
    }
}
