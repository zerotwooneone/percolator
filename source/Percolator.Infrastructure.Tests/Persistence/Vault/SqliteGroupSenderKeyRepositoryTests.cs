namespace Percolator.Infrastructure.Tests.Persistence.Vault;

using System;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;

[TestFixture]
public class SqliteGroupSenderKeyRepositoryTests
{
    private SqliteTestDb _testDb = null!;
    private SqliteGroupSenderKeyRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        _testDb = new SqliteTestDb();
        _repository = new SqliteGroupSenderKeyRepository(_testDb.DbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _testDb.Dispose();
    }

    [Test]
    public async Task AtomicCommit_SavesAndRetrievesSenderKeyRatchet()
    {
        // Arrange
        var channelId = ChannelId.New();
        var authorId = PublicIdentityId.New();
        var deviceId = DeviceId.Primary;

        byte[] chainBytes = new byte[32];
        Array.Fill(chainBytes, (byte)0x55);
        using var chainKey = ChainKey.FromSpan(chainBytes);

        var ratchet = new GroupSenderKeyRatchet(
            channelId,
            authorId,
            deviceId,
            chainKey,
            initialIteration: 3,
            keyId: 10);

        // Act
        await _repository.SaveSenderKeyRatchetAsync(ratchet);

        // Assert - Reload in fresh context
        using var newContext = _testDb.CreateNewContext();
        var newRepo = new SqliteGroupSenderKeyRepository(newContext);
        var loaded = await newRepo.GetSenderKeyRatchetAsync(channelId, authorId, deviceId);

        loaded.Should().NotBeNull();
        loaded!.ChannelId.Should().Be(channelId);
        loaded.AuthorId.Should().Be(authorId);
        loaded.AuthorDeviceId.Should().Be(deviceId);
        loaded.Iteration.Should().Be(3);
        loaded.KeyId.Should().Be(10);
        loaded.CurrentChainKey.Should().NotBeNull();
        loaded.CurrentChainKey!.Span.ToArray().Should().Equal(chainBytes);
    }
}
