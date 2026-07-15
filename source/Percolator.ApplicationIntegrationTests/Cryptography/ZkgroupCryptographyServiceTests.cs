using FluentAssertions;
using NUnit.Framework;
using Percolator.Cryptography;
using Percolator.Infrastructure.Cryptography;
using System.Text;

namespace Percolator.ApplicationIntegrationTests.Cryptography;

[TestFixture]
public class ZkgroupCryptographyServiceTests
{
    private ZkgroupCryptographyService _sut;

    [SetUp]
    public void SetUp()
    {
        _sut = new ZkgroupCryptographyService();
    }

    [Test]
    public void GenerateGroupMasterKey_WhenCalledWithValidRandomness_ReturnsValidMasterKey()
    {
        // Arrange
        var randomness = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(randomness);

        // Act
        var masterKey = _sut.GenerateGroupMasterKey(randomness);

        // Assert
        masterKey.Should().NotBeNull();
        masterKey.Span.Length.Should().Be(32);
    }

    [Test]
    public void SerializeAndDeserializeGroupMasterKey_WhenValid_RoundTripsSuccessfully()
    {
        // Arrange
        var randomness = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(randomness);
        var originalMasterKey = _sut.GenerateGroupMasterKey(randomness);

        // Act
        var serialized = _sut.SerializeGroupMasterKey(originalMasterKey);
        var deserialized = _sut.DeserializeGroupMasterKey(serialized);

        // Assert
        deserialized.Span.ToArray().Should().BeEquivalentTo(originalMasterKey.Span.ToArray());
    }

    [Test]
    public void DeriveGroupId_WhenCalledWithValidMasterKey_ReturnsValidGroupId()
    {
        // Arrange
        var randomness = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(randomness);
        var masterKey = _sut.GenerateGroupMasterKey(randomness);

        // Act
        var groupId = _sut.DeriveGroupId(masterKey);

        // Assert
        groupId.Should().NotBeNull();
        groupId.Span.Length.Should().Be(32);
    }

    [Test]
    public void DeriveBlobKey_WhenCalledWithValidMasterKey_ReturnsValidBlobKey()
    {
        // Arrange
        var randomness = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(randomness);
        var masterKey = _sut.GenerateGroupMasterKey(randomness);

        // Act
        var blobKey = _sut.DeriveBlobKey(masterKey);

        // Assert
        blobKey.Should().NotBeNull();
        blobKey.Span.Length.Should().Be(32);
    }

    [Test]
    public void EncryptAndDecryptGroupProfile_WhenValid_RoundTripsSuccessfully()
    {
        // Arrange
        var randomness = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(randomness);
        var masterKey = _sut.GenerateGroupMasterKey(randomness);

        var profileData = Encoding.UTF8.GetBytes("Test Profile Name and Avatar Content");
        var profilePlaintext = ProfilePlaintextBytes.FromBytesOwned(profileData);

        // Act
        var ciphertext = _sut.EncryptGroupProfile(masterKey, profilePlaintext);
        var decryptedPlaintext = _sut.DecryptGroupProfile(masterKey, ciphertext);

        // Assert
        decryptedPlaintext.Span.ToArray().Should().BeEquivalentTo(profileData);
    }

    [Test]
    public void DeriveGroupPublicParams_WhenCalledWithValidMasterKey_ReturnsValidParams()
    {
        // Arrange
        var randomness = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(randomness);
        var masterKey = _sut.GenerateGroupMasterKey(randomness);

        // Act
        var publicParams = _sut.DeriveGroupPublicParams(masterKey);

        // Assert
        publicParams.Should().NotBeNull();
        publicParams.Span.Length.Should().BeGreaterThan(0);
    }
}
