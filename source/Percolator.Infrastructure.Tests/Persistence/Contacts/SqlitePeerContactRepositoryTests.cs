namespace Percolator.Infrastructure.Tests.Persistence.Contacts;

using System;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;

[TestFixture]
public class SqlitePeerContactRepositoryTests
{
    private SqliteTestDb _testDb = null!;
    private SqlitePeerContactRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        _testDb = new SqliteTestDb();
        _repository = new SqlitePeerContactRepository(_testDb.DbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _testDb.Dispose();
    }

    [Test]
    public async Task SaveContact_UpdatesTrustLevelAndTimestamps()
    {
        // Arrange
        var ownerId = PublicIdentityId.New();
        var peerId = PublicIdentityId.New();
        var nickname = ContactNickname.Create("Alice", peerId);

        var contact = PeerContact.Rehydrate(
            ownerId,
            peerId,
            nickname,
            PeerTrustLevel.Tofu,
            ContactState.Active,
            DateTimeOffset.UtcNow.AddDays(-1),
            lastSeenAtUtc: null,
            primaryPublicKey: null);

        // Act - Save new contact
        await _repository.SaveAsync(contact);

        var retrieved = await _repository.GetByPeerIdAsync(ownerId, peerId);
        retrieved.Should().NotBeNull();
        retrieved!.TrustLevel.Should().Be(PeerTrustLevel.Tofu);
        retrieved.Nickname.Value.Should().Be("Alice");

        // Act - Update trust level to Verified and add last seen
        var now = DateTimeOffset.UtcNow;
        var updatedContact = PeerContact.Rehydrate(
            ownerId,
            peerId,
            nickname,
            PeerTrustLevel.Verified,
            ContactState.Active,
            contact.CreatedAtUtc,
            lastSeenAtUtc: now,
            primaryPublicKey: null);

        await _repository.SaveAsync(updatedContact);

        // Assert - Reload in fresh context
        using var newContext = _testDb.CreateNewContext();
        var newRepo = new SqlitePeerContactRepository(newContext);
        var reloaded = await newRepo.GetByPeerIdAsync(ownerId, peerId);

        reloaded.Should().NotBeNull();
        reloaded!.TrustLevel.Should().Be(PeerTrustLevel.Verified);
        reloaded.LastSeenAtUtc.Should().BeCloseTo(now, TimeSpan.FromSeconds(1));
    }
}
