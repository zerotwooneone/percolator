using Percolator.Identity;

namespace Percolator.IdentityTests;

[TestFixture]
public class ProfilePrimitivesTests
{
    private static byte[] Bytes(params byte[] b) => b;

    [Test]
    public void EncryptedProfileDataBytes_ThrowsException_WhenPayloadExceedsMaxLength()
    {
        // Arrange
        var oversizedPayload = new byte[65536]; // Exceeds typical maximum for encrypted profile data

        // Act + Assert
        Assert.Throws<ArgumentException>(() => EncryptedProfileDataBytes.FromBytesOwned(oversizedPayload));
    }

    [Test]
    public void ProfileKeyBytes_AcceptsValid32ByteKey()
    {
        // Arrange
        var validKey = Bytes(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32);

        // Act
        var profileKey = ProfileKeyBytes.FromBytesOwned(validKey);

        // Assert
        Assert.That(profileKey.Length, Is.EqualTo(32));
    }

    [Test]
    public void ProfileKeyBytes_ThrowsException_WhenKeyIsNot32Bytes()
    {
        // Arrange
        var invalidKey = Bytes(1, 2, 3); // Only 3 bytes

        // Act + Assert
        Assert.Throws<ArgumentException>(() => ProfileKeyBytes.FromBytesOwned(invalidKey));
    }

    [Test]
    public void DeviceId_Primary_IsCorrectlyInitialized()
    {
        // Act
        var deviceId = DeviceId.Primary;

        // Assert
        Assert.That(deviceId.Value, Is.EqualTo(1));
    }
}
