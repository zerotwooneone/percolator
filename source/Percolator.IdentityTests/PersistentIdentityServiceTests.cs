using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Percolator.Identity;
using Percolator.Identity.Model;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Percolator.IdentityTests;

[TestFixture]
public class PersistentIdentityServiceTests
{
    private Mock<IIdentityStore> _mockIdentityStore;
    private Mock<IKeyManagementService> _mockKeyManagementService;
    private PersistentIdentityService _service;

    [SetUp]
    public void SetUp()
    {
        _mockIdentityStore = new Mock<IIdentityStore>();
        _mockKeyManagementService = new Mock<IKeyManagementService>();
        _service = new PersistentIdentityService(
            _mockIdentityStore.Object,
            _mockKeyManagementService.Object,
            Mock.Of<ILogger<PersistentIdentityService>>());
    }

    [Test]
    public async Task CreateIdentityAsync_Should_Create_And_Store_Identity_When_Name_Is_Unique()
    {
        // Arrange
        var identityName = "test-identity";
        var nickname = "nickname";
        _mockIdentityStore.Setup(s => s.IdentityExistsAsync(identityName, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _mockIdentityStore.Setup(s => s.StoreIdentityAsync(It.IsAny<IdentityRecord>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        var result = await _service.CreateIdentityAsync(identityName, nickname);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().NotBeEmpty();
        result.Name.Should().Be(identityName);
        result.Nickname.Should().Be(nickname);
        _mockIdentityStore.Verify(s => s.StoreIdentityAsync(It.Is<IdentityRecord>(i => i.Name == identityName), It.IsAny<CancellationToken>()), Times.Once);
        _mockKeyManagementService.Verify(s => s.GetOrCreateKeysAsync(identityName), Times.Once);
    }

    [Test]
    public async Task CreateIdentityAsync_Should_Throw_When_Identity_Exists()
    {
        // Arrange
        var identityName = "existing-identity";
        _mockIdentityStore.Setup(s => s.IdentityExistsAsync(identityName, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        // Act & Assert
        await _service.Invoking(s => s.CreateIdentityAsync(identityName, null)).Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task GetIdentityRecordAsync_Should_Return_Record_When_Identity_Exists()
    {
        // Arrange
        var identityName = "test-identity";
        var identityRecord = new IdentityRecord(Guid.NewGuid(), identityName, "nickname");
        _mockIdentityStore.Setup(s => s.GetIdentityAsync(identityName, It.IsAny<CancellationToken>())).ReturnsAsync(identityRecord);

        // Act
        var result = await _service.GetIdentityRecordAsync(identityName);

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be(identityName);
    }

    [Test]
    public async Task GetIdentityRecordAsync_Should_Return_Null_When_Identity_Does_Not_Exist()
    {
        // Arrange
        var identityName = "non-existent-identity";
        _mockIdentityStore.Setup(s => s.GetIdentityAsync(identityName, It.IsAny<CancellationToken>())).ReturnsAsync((IdentityRecord?)null);

        // Act
        var result = await _service.GetIdentityRecordAsync(identityName);

        // Assert
        result.Should().BeNull();
    }

    [Test]
    public async Task ListIdentityNamesAsync_Should_Return_All_Identity_Names()
    {
        // Arrange
        var names = new List<string> { "id1", "id2", "id3" };
        _mockIdentityStore.Setup(s => s.ListIdentityNamesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(names);

        // Act
        var result = await _service.ListIdentityNamesAsync();

        // Assert
        result.Should().BeEquivalentTo(names);
    }
}
