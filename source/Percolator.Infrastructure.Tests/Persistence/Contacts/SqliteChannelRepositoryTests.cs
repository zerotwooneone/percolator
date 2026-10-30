namespace Percolator.Infrastructure.Tests.Persistence.Contacts;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;

[TestFixture]
public class SqliteChannelRepositoryTests
{
    private SqliteTestDb _testDb = null!;
    private SqliteChannelRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        _testDb = new SqliteTestDb();
        _repository = new SqliteChannelRepository(_testDb.DbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _testDb.Dispose();
    }

    [Test]
    public async Task SaveChannel_PersistsParticipants()
    {
        // Arrange
        var channelId = ChannelId.New();
        var adminId = PublicIdentityId.New();
        var memberId = PublicIdentityId.New();
        var now = DateTimeOffset.UtcNow;

        var members = new List<ChannelMember>
        {
            new ChannelMember(adminId, ChannelRole.Admin, now),
            new ChannelMember(memberId, ChannelRole.Member, now)
        };

        var group = GroupChannel.Rehydrate(
            channelId,
            "General Chat",
            EpochNumber.Genesis,
            now,
            now,
            members);

        // Act - Save group channel
        await _repository.SaveGroupAsync(group);

        // Assert - Reload in fresh context
        using var newContext = _testDb.CreateNewContext();
        var newRepo = new SqliteChannelRepository(newContext);
        var loaded = await newRepo.GetGroupByIdAsync(channelId);

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(channelId);
        loaded.Name.Should().Be("General Chat");
        loaded.Members.Should().HaveCount(2);
        loaded.Members.Should().Contain(m => m.Id == adminId && m.Role == ChannelRole.Admin);
        loaded.Members.Should().Contain(m => m.Id == memberId && m.Role == ChannelRole.Member);

        // Assert - Query all groups for member
        var groups = await newRepo.GetAllGroupsForOwnerAsync(memberId);
        groups.Should().ContainSingle(g => g.Id == channelId);
    }

    [Test]
    public async Task SaveDirectAsync_PersistsDirectChannelAndPayloads()
    {
        // Arrange
        var channelId = ChannelId.New();
        var ownerId = PublicIdentityId.New();
        var peerId = PublicIdentityId.New();
        var now = DateTimeOffset.UtcNow;

        var payload = new ChannelPayload(
            PayloadId.New(),
            channelId,
            ownerId,
            DeviceId.Primary,
            new byte[] { 1, 2, 3, 4 },
            now);

        var direct = DirectChannel.Rehydrate(
            channelId,
            ownerId,
            peerId,
            now,
            now,
            new[] { payload });

        // Act
        await _repository.SaveDirectAsync(direct);

        // Assert - Reload
        using var newContext = _testDb.CreateNewContext();
        var newRepo = new SqliteChannelRepository(newContext);
        var loaded = await newRepo.GetDirectByIdAsync(channelId);

        loaded.Should().NotBeNull();
        loaded!.OwnerIdentityId.Should().Be(ownerId);
        loaded.RemotePeerId.Should().Be(peerId);
        loaded.Payloads.Should().ContainSingle(p => p.Id == payload.Id);
    }
}
