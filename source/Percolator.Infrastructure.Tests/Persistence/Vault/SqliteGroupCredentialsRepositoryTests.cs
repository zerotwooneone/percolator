namespace Percolator.Infrastructure.Tests.Persistence.Vault;

using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Channels.ValueObjects;
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
