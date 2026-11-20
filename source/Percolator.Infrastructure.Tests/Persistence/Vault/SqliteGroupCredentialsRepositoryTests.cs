namespace Percolator.Infrastructure.Tests.Persistence.Vault;

using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;

[TestFixture]
public class SqliteGroupCredentialsRepositoryTests
{
    private SqliteTestDb _testDb = null!;
    private SqliteGroupCredentialsRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        _testDb = new SqliteTestDb();
        _repository = new SqliteGroupCredentialsRepository(_testDb.DbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _testDb.Dispose();
    }

    [Test]
    public async Task SaveAndGetAsync_RoundtripsSuccessfully()
    {
        // Arrange
        var channelId = ChannelId.New();
        byte[] masterBytes = new byte[32];
        RandomNumberGenerator.Fill(masterBytes);
        var masterKey = GroupMasterKey.FromSpan(masterBytes);

        byte[] macBytes = new byte[32];
        RandomNumberGenerator.Fill(macBytes);
        var authMac = AuthCredentialMacBytes.FromSpan(macBytes);

        byte[] blobBytes = new byte[32];
        RandomNumberGenerator.Fill(blobBytes);
        var blobKey = BlobKey.FromSpan(blobBytes);

        var credentials = new GroupCredentials(channelId, masterKey, authMac, blobKey);

        // Act - Save credentials
        await _repository.SaveAsync(credentials);

        // Assert - Reload in fresh context
        using var newContext = _testDb.CreateNewContext();
        var newRepo = new SqliteGroupCredentialsRepository(newContext);
        var loaded = await newRepo.GetAsync(channelId);

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(channelId);
        loaded.MasterKey.Span.ToArray().Should().Equal(masterBytes);
        loaded.AuthCredentialMac.Span.ToArray().Should().Equal(macBytes);
        loaded.LocalBlobKey.Should().NotBeNull();
        loaded.LocalBlobKey!.Span.ToArray().Should().Equal(blobBytes);
        loaded.CurrentEpoch.Value.Should().Be(0);
    }

    [Test]
    public async Task SaveAndGetAsync_WithEpoch_RoundtripsCurrentEpochSuccessfully()
    {
        // Arrange
        var channelId = ChannelId.New();
        byte[] masterBytes = new byte[32];
        RandomNumberGenerator.Fill(masterBytes);
        var masterKey = GroupMasterKey.FromSpan(masterBytes);

        byte[] macBytes = new byte[32];
        RandomNumberGenerator.Fill(macBytes);
        var authMac = AuthCredentialMacBytes.FromSpan(macBytes);

        var credentials = new GroupCredentials(channelId, masterKey, authMac, null, currentEpoch: new EpochNumber(42));

        // Act - Save credentials with epoch 42
        await _repository.SaveAsync(credentials);

        // Assert - Reload in fresh context
        using var newContext1 = _testDb.CreateNewContext();
        var newRepo1 = new SqliteGroupCredentialsRepository(newContext1);
        var loaded1 = await newRepo1.GetAsync(channelId);

        loaded1.Should().NotBeNull();
        loaded1!.CurrentEpoch.Value.Should().Be(42);
        loaded1.AuthCredentialMac.Span.ToArray().Should().Equal(macBytes);

        // Act - Update epoch to 43 and resave
        var updateResult = loaded1.UpdateEpoch(new EpochNumber(43));
        updateResult.IsSuccess.Should().BeTrue();
        await newRepo1.SaveAsync(loaded1);

        // Assert - Reload in another context
        using var newContext2 = _testDb.CreateNewContext();
        var newRepo2 = new SqliteGroupCredentialsRepository(newContext2);
        var loaded2 = await newRepo2.GetAsync(channelId);

        loaded2.Should().NotBeNull();
        loaded2!.CurrentEpoch.Value.Should().Be(43);
        loaded2.AuthCredentialMac.Span.ToArray().Should().Equal(macBytes);
    }

    [Test]
    public void UpdateEpoch_RegressingEpoch_ReturnsRollbackProhibitedError()
    {
        // Arrange
        var channelId = ChannelId.New();
        var masterKey = GroupMasterKey.FromSpan(new byte[32]);
        var authMac = AuthCredentialMacBytes.FromSpan(new byte[32]);
        var credentials = new GroupCredentials(channelId, masterKey, authMac, currentEpoch: new EpochNumber(10));

        // Act
        var result = credentials.UpdateEpoch(new EpochNumber(9));

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("EPOCH_ROLLBACK_PROHIBITED");
        credentials.CurrentEpoch.Value.Should().Be(10);
    }

    [Test]
    public async Task DeleteAsync_RemovesCredentials()
    {
        // Arrange
        var channelId = ChannelId.New();
        byte[] masterBytes = new byte[32];
        RandomNumberGenerator.Fill(masterBytes);
        var masterKey = GroupMasterKey.FromSpan(masterBytes);

        byte[] macBytes = new byte[32];
        RandomNumberGenerator.Fill(macBytes);
        var authMac = AuthCredentialMacBytes.FromSpan(macBytes);

        var credentials = new GroupCredentials(channelId, masterKey, authMac, null);
        await _repository.SaveAsync(credentials);

        // Act - Delete
        await _repository.DeleteAsync(channelId);

        // Assert
        using var newContext = _testDb.CreateNewContext();
        var newRepo = new SqliteGroupCredentialsRepository(newContext);
        var loaded = await newRepo.GetAsync(channelId);
        loaded.Should().BeNull();
    }
}
