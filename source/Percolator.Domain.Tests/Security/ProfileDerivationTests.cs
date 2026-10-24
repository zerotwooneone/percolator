using System.Security.Cryptography;
using NUnit.Framework;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Domain.Tests.TestDoubles;

namespace Percolator.Domain.Tests.Security;

[TestFixture]
public sealed class ProfileDerivationTests
{
    private DeterministicCryptoEngine _cryptoEngine = null!;

    [SetUp]
    public void SetUp()
    {
        _cryptoEngine = new DeterministicCryptoEngine();
    }

    [Test]
    public void GenerateProfileKey_Returns32Bytes_AndZeroizesOnDispose()
    {
        ProfileKey profileKey;
        byte[] originalCopy;
        using (profileKey = ProfileKey.Generate())
        {
            Assert.That(profileKey.Length, Is.EqualTo(32));
            originalCopy = profileKey.ToArray();
            Assert.That(originalCopy, Is.Not.EqualTo(new byte[32]));
        }

        Assert.That(profileKey.Span.ToArray(), Is.EqualTo(new byte[32]));
    }

    [Test]
    public void Derivations_AreDeterministic_AndEnforceDomainSeparation()
    {
        var ikBytes = new byte[32];
        ikBytes[0] = 0xAA;
        var identityKey = IdentityKey.FromSpan(ikBytes);

        var keyBytes = new byte[32];
        keyBytes[0] = 0x55;
        using var profileKey = ProfileKey.FromSpan(keyBytes);

        var locator1 = BlindedProfileLocator.Derive(identityKey, profileKey, _cryptoEngine);
        var locator2 = BlindedProfileLocator.Derive(identityKey, profileKey, _cryptoEngine);
        Assert.That(locator1, Is.EqualTo(locator2));

        var writeToken1 = ProfileWriteToken.Derive(identityKey, profileKey, _cryptoEngine);
        var writeToken2 = ProfileWriteToken.Derive(identityKey, profileKey, _cryptoEngine);
        Assert.That(writeToken1, Is.EqualTo(writeToken2));

        var deliveryToken1 = DeliveryToken.DeriveFromProfileKey(identityKey, profileKey, _cryptoEngine);
        var deliveryToken2 = DeliveryToken.DeriveFromProfileKey(identityKey, profileKey, _cryptoEngine);
        Assert.That(deliveryToken1, Is.EqualTo(deliveryToken2));

        using var avatarKey1 = AvatarKey.Derive(profileKey, _cryptoEngine);
        using var avatarKey2 = AvatarKey.Derive(profileKey, _cryptoEngine);
        Assert.That(avatarKey1, Is.EqualTo(avatarKey2));

        // Domain separation checks
        Assert.That(locator1.Span.ToArray(), Is.Not.EqualTo(writeToken1.Span.ToArray()));
        Assert.That(locator1.Span.ToArray(), Is.Not.EqualTo(deliveryToken1.Span.ToArray()));
        Assert.That(writeToken1.Span.ToArray(), Is.Not.EqualTo(deliveryToken1.Span.ToArray()));
        Assert.That(locator1.Span.ToArray(), Is.Not.EqualTo(avatarKey1.Span.ToArray()));
    }

    [Test]
    public void AvatarKey_ZeroizesOnDispose()
    {
        AvatarKey avatarKey;
        using var profileKey = ProfileKey.Generate();
        using (avatarKey = AvatarKey.Derive(profileKey, _cryptoEngine))
        {
            Assert.That(avatarKey.Length, Is.EqualTo(32));
            Assert.That(avatarKey.Span.ToArray(), Is.Not.EqualTo(new byte[32]));
        }

        Assert.That(avatarKey.Span.ToArray(), Is.EqualTo(new byte[32]));
    }
}
