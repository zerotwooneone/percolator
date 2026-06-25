using Percolator.Identity;
using Percolator.Identity.Model;

namespace Percolator.IdentityTests;

[TestFixture]
public class SelfIdentityTests
{
    private static byte[] Bytes(params byte[] b) => b;

    [Test]
    public void GetActiveKey_ReturnsKeyWithinWindow_OtherwiseNull()
    {
        // Arrange
        var now = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var id = new SelfIdentity(new SelfId(1), new PublicIdentityId(Guid.NewGuid()), new ListeningPort(5000));
        id.AddKey(spki: Bytes(1, 2, 3), notBefore: now, expiresAt: now.AddDays(1), now: now);

        // Act + Assert
        Assert.That(id.GetActiveKey(now.AddMinutes(-1)), Is.Null);
        Assert.That(id.GetActiveKey(now), Is.Not.Null);
        Assert.That(id.GetActiveKey(now.AddHours(12)), Is.Not.Null);
        Assert.That(id.GetActiveKey(now.AddDays(2)), Is.Null);
    }

    [Test]
    public void AddKey_ThrowsWhenOverlappingActiveAtNow()
    {
        // Arrange
        var now = new DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var id = new SelfIdentity(new SelfId(2), new PublicIdentityId(Guid.NewGuid()), new ListeningPort(5000));
        id.AddKey(spki: Bytes(9), notBefore: now, expiresAt: now.AddDays(1), now: now);

        // Act + Assert: Attempt to add a second key that would also be active at 'now'
        Assert.Throws<InvalidOperationException>(() =>
            id.AddKey(spki: Bytes(8), notBefore: now, expiresAt: now.AddDays(2), now: now));
    }

    [Test]
    public void TouchLastUsed_SetsLastUsedUtc()
    {
        // Arrange
        var id = new SelfIdentity(new SelfId(3), new PublicIdentityId(Guid.NewGuid()), new ListeningPort(5000));
        var t1 = new DateTimeOffset(2025, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var t2 = t1.AddHours(1);

        // Act
        id.TouchLastUsed(t1);
        
        // Assert
        Assert.That(id.LastUsedUtc, Is.EqualTo(t1));
        id.TouchLastUsed(t2);
        Assert.That(id.LastUsedUtc, Is.EqualTo(t2));
    }

    [Test]
    public void GetNextScheduledKey_ReturnsEarliestFutureKey()
    {
        // Arrange
        var now = new DateTimeOffset(2025, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var id = new SelfIdentity(new SelfId(4), new PeerId(Guid.NewGuid()), new ListeningPort(5000));
        id.AddKey(Bytes(1), notBefore: now, expiresAt: now.AddDays(1), now: now);
        id.AddKey(Bytes(2), notBefore: now.AddDays(2), expiresAt: now.AddDays(3), now: now);

        // Act
        var next = id.GetNextScheduledKey(now.AddDays(1));

        // Assert
        Assert.That(next, Is.Not.Null);
        Assert.That(next!.NotBefore, Is.EqualTo(now.AddDays(2)));
    }

    [Test]
    public void AddKey_AllowsRotationAtBoundaryWithoutOverlap()
    {
        // Arrange
        var now = new DateTimeOffset(2025, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var id = new SelfIdentity(new SelfId(5), new PeerId(Guid.NewGuid()), new ListeningPort(5000));
        id.AddKey(Bytes(1), notBefore: now, expiresAt: now.AddDays(1), now: now);

        // Act: schedule next key to start exactly when previous expires
        id.AddKey(Bytes(2), notBefore: now.AddDays(1), expiresAt: now.AddDays(2), now: now);

        // Assert: active now is still first; future next is the boundary key
        Assert.That(id.GetActiveKey(now), Is.Not.Null);
        var next = id.GetNextScheduledKey(now);
        Assert.That(next, Is.Not.Null);
        Assert.That(next!.NotBefore, Is.EqualTo(now.AddDays(1)));
    }

    [Test]
    public void CommitProfileUpdate_IncrementsRevisionAndUpdatesPayload()
    {
        // Arrange
        var id = new SelfIdentity(new SelfId(6), new PeerId(Guid.NewGuid()), new ListeningPort(5000));
        var initialRevision = id.ProfileRevision;
        var newKey = ProfileKeyBytes.FromBytesOwned(Bytes(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32));
        var ciphertext = EncryptedProfileDataBytes.FromBytesOwned(Bytes(10, 20, 30));
        var nonce = ProfileNonceBytes.FromBytesOwned(Bytes(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12));
        var tag = ProfileTagBytes.FromBytesOwned(Bytes(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16));
        var package = new ProfileCiphertextPackage(ciphertext, nonce, tag);

        // Act
        id.CommitProfileUpdate(newKey, package);

        // Assert
        Assert.That(id.ProfileRevision, Is.EqualTo(initialRevision + 1));
        Assert.That(id.CurrentProfileKey, Is.EqualTo(newKey));
        Assert.That(id.CurrentProfileCiphertext, Is.EqualTo(package));
    }
}
