using FluentAssertions;
using Percolator.Application.Identity;
using Percolator.Identity.Model;
using Percolator.Network;
using System.Security.Cryptography;

namespace Percolator.ApplicationTests;

// A stable, application-level identity that can own multiple cryptographic keys.
// Removed the ApplicationIdentity record as it is replaced with IdentityRecord

[TestFixture]
public class InMemoryIdentityRepositoryTests
{
    private InMemoryIdentityRepository _sut;

    // Test data
    private static readonly IdentityRecord _testIdentity1 = new(Guid.NewGuid(), "Alice");
    private static readonly PublicKeyHash _keyHash1 = new(SHA256.HashData("key1"u8.ToArray()));
    private static readonly PublicKeyHash _keyHash2 = new(SHA256.HashData("key2"u8.ToArray()));

    [SetUp]
    public void Setup()
    {
        _sut = new InMemoryIdentityRepository();
    }

    [Test]
    public async Task GetIdentityForPublicKeyAsync_ShouldReturnNull_WhenKeyIsUnknown()
    {
        // Act
        var result = await _sut.GetIdentityForPublicKeyAsync(_keyHash1);

        // Assert
        result.Should().BeNull();
    }

    [Test]
    public async Task AssociatePublicKeyWithIdentityAsync_ShouldAllowRetrieval()
    {
        // Arrange
        await _sut.AssociatePublicKeyWithIdentityAsync(_keyHash1, _testIdentity1);

        // Act
        var result = await _sut.GetIdentityForPublicKeyAsync(_keyHash1);

        // Assert
        result.Should().Be(_testIdentity1);
    }

    [Test]
    public async Task AssociatePublicKeyWithIdentityAsync_ShouldAllowMultipleKeysForOneIdentity()
    {
        // Arrange
        await _sut.AssociatePublicKeyWithIdentityAsync(_keyHash1, _testIdentity1);
        await _sut.AssociatePublicKeyWithIdentityAsync(_keyHash2, _testIdentity1);

        // Act
        var result1 = await _sut.GetIdentityForPublicKeyAsync(_keyHash1);
        var result2 = await _sut.GetIdentityForPublicKeyAsync(_keyHash2);

        // Assert
        result1.Should().Be(_testIdentity1);
        result2.Should().Be(_testIdentity1);
    }

    [Test]
    public async Task AssociatePublicKeyWithIdentityAsync_ShouldUpdateExistingAssociation()
    {
        // Arrange
        var identity2 = new IdentityRecord(Guid.NewGuid(), "Bob");
        await _sut.AssociatePublicKeyWithIdentityAsync(_keyHash1, _testIdentity1);

        // Act: Re-associate the same key with a different identity
        await _sut.AssociatePublicKeyWithIdentityAsync(_keyHash1, identity2);
        var result = await _sut.GetIdentityForPublicKeyAsync(_keyHash1);

        // Assert
        result.Should().Be(identity2);
    }
}
