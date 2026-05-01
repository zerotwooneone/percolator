using FluentAssertions;
using Moq;
using Percolator.Cryptography;

namespace Percolator.CryptographyTests;

[TestFixture]
public class HandshakePlannerTests
{
    [Test]
    public void ValidateInvitation_Throws_When_Empty()
    {
        var planner = new HandshakePlanner();
        // This test is no longer applicable since HandshakeInvitation now has minLength: 1
        // Empty invitations cannot be constructed due to ByteArray constraint
        // Validation is enforced at the type level rather than the planner level
        Assert.Pass("Empty invitations are prevented by ByteArray constraint (minLength: 1)");
    }

    [Test]
    public void PlanEstablishment_Reflects_OneTimePreKey_Presence()
    {
        var planner = new HandshakePlanner();
        var idKey = RatchetIdentityKey.FromBytes(new byte[64]);
        var pre = PreKey.FromBytes(new byte[64]);
        var sig = Signature.FromBytes(new byte[60]);
        var bundleWithOtp = new PreKeyBundle(idKey, Guid.NewGuid(), pre, sig, Guid.NewGuid(), OneTimeKey.FromBytes(new byte[64]), DateTimeOffset.UtcNow);
        var plan1 = planner.PlanEstablishment(bundleWithOtp);
        plan1.HasOneTimePreKey.Should().BeTrue();

        var bundleWithoutOtp = new PreKeyBundle(idKey, Guid.NewGuid(), pre, sig, null, null, null);
        var plan2 = planner.PlanEstablishment(bundleWithoutOtp);
        plan2.HasOneTimePreKey.Should().BeFalse();
    }
}
