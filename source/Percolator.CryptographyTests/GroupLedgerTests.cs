using Percolator.Cryptography;
using Percolator.Cryptography.GroupLedger;

namespace Percolator.CryptographyTests;

[TestFixture]
public class GroupLedgerTests
{
    [Test]
    public void GroupCredentials_CanBeInstantiated()
    {
        // Arrange
        var groupId = GroupId.FromBytesOwned(new byte[32]);
        var masterKey = GroupMasterKey.FromBytesOwned(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32 });
        var authCredentialMac = AuthCredentialMacBytes.FromBytesOwned(new byte[] { 1, 2, 3 });

        // Act
        var credentials = new GroupCredentials(groupId, masterKey, authCredentialMac);

        // Assert
        Assert.That(credentials.Id, Is.EqualTo(groupId));
        Assert.That(credentials.MasterKey, Is.EqualTo(masterKey));
        Assert.That(credentials.AuthCredentialMac, Is.EqualTo(authCredentialMac));
    }

    [Test]
    public void SenderKeyRatchet_CanBeInstantiated()
    {
        // Arrange
        var groupId = GroupId.FromBytesOwned(new byte[32]);
        var authorId = new CryptoPublicIdentityId(Guid.NewGuid());
        var keyId = 1u;
        var chainKey = ChainKey.FromBytesOwned(new byte[32]);
        var signatureKey = SignaturePublicKey.FromBytesOwned(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32 });

        // Act
        var ratchet = new SenderKeyRatchet(groupId, authorId, keyId, chainKey, signatureKey);

        // Assert
        Assert.That(ratchet.Id, Is.EqualTo(groupId));
        Assert.That(ratchet.AuthorPublicIdentityId, Is.EqualTo(authorId));
        Assert.That(ratchet.KeyId, Is.EqualTo(keyId));
        Assert.That(ratchet.ChainKey, Is.EqualTo(chainKey));
        Assert.That(ratchet.SignatureKey, Is.EqualTo(signatureKey));
    }

    [Test]
    public void UnknownMessageCache_CanBeInstantiated()
    {
        // Arrange
        var groupId = GroupId.FromBytesOwned(new byte[32]);
        var missingKeyId = 1u;
        var ciphertext = Ciphertext.FromBytesOwned(new byte[] { 1, 2, 3 });
        var receivedAtUtc = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);

        // Act
        var cache = new UnknownMessageCache(1, groupId, missingKeyId, ciphertext, receivedAtUtc);

        // Assert
        Assert.That(cache.Id, Is.EqualTo(1));
        Assert.That(cache.GroupId, Is.EqualTo(groupId));
        Assert.That(cache.MissingKeyId, Is.EqualTo(missingKeyId));
        Assert.That(cache.Ciphertext, Is.EqualTo(ciphertext));
        Assert.That(cache.ReceivedAtUtc, Is.EqualTo(receivedAtUtc));
    }

    [Test]
    public void SkippedMessageKey_CanBeInstantiated()
    {
        // Arrange
        var groupId = GroupId.FromBytesOwned(new byte[32]);
        var keyId = 1u;
        var messageIndex = 5;
        var messageKey = new byte[] { 1, 2, 3 };

        // Act
        var skippedKey = new SkippedMessageKey(groupId, keyId, messageIndex, messageKey);

        // Assert
        Assert.That(skippedKey.GroupId, Is.EqualTo(groupId));
        Assert.That(skippedKey.KeyId, Is.EqualTo(keyId));
        Assert.That(skippedKey.MessageIndex, Is.EqualTo(messageIndex));
        Assert.That(skippedKey.MessageKey, Is.EqualTo(messageKey));
    }

    [Test]
    public void UnknownMessageCache_WhenCapacityNotExceeded_DoesNotEvict()
    {
        // Arrange
        var groupId = GroupId.FromBytesOwned(new byte[32]);
        var missingKeyId = 1u;
        var ciphertext = Ciphertext.FromBytesOwned(new byte[] { 1, 2, 3 });
        var receivedAtUtc = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var cache = new UnknownMessageCache(1, groupId, missingKeyId, ciphertext, receivedAtUtc);
        var oldestReceivedAtUtc = receivedAtUtc;

        // Act
        var shouldEvict = cache.ShouldEvict(50, oldestReceivedAtUtc);

        // Assert
        Assert.That(shouldEvict, Is.False);
    }

    [Test]
    public void UnknownMessageCache_WhenCapacityExceededAndIsOldest_Evicts()
    {
        // Arrange
        var groupId = GroupId.FromBytesOwned(new byte[32]);
        var missingKeyId = 1u;
        var ciphertext = Ciphertext.FromBytesOwned(new byte[] { 1, 2, 3 });
        var receivedAtUtc = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var cache = new UnknownMessageCache(1, groupId, missingKeyId, ciphertext, receivedAtUtc);
        var oldestReceivedAtUtc = receivedAtUtc;

        // Act
        var shouldEvict = cache.ShouldEvict(101, oldestReceivedAtUtc);

        // Assert
        Assert.That(shouldEvict, Is.True);
    }

    [Test]
    public void UnknownMessageCache_WhenCapacityExceededAndNotOldest_DoesNotEvict()
    {
        // Arrange
        var groupId = GroupId.FromBytesOwned(new byte[32]);
        var missingKeyId = 1u;
        var ciphertext = Ciphertext.FromBytesOwned(new byte[] { 1, 2, 3 });
        var receivedAtUtc = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var cache = new UnknownMessageCache(1, groupId, missingKeyId, ciphertext, receivedAtUtc);
        var oldestReceivedAtUtc = receivedAtUtc.AddMinutes(-1);

        // Act
        var shouldEvict = cache.ShouldEvict(101, oldestReceivedAtUtc);

        // Assert
        Assert.That(shouldEvict, Is.False);
    }
}
