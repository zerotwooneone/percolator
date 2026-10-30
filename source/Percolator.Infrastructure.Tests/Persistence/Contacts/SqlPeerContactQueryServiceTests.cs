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
public class SqlPeerContactQueryServiceTests
{
    private SqliteTestDb _testDb = null!;
    private SqlitePeerContactRepository _repository = null!;
    private SqlPeerContactQueryService _queryService = null!;

    [SetUp]
    public void SetUp()
    {
        _testDb = new SqliteTestDb();
        _repository = new SqlitePeerContactRepository(_testDb.DbContext);
        _queryService = new SqlPeerContactQueryService(_testDb.DbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _testDb.Dispose();
    }

    [Test]
    public async Task GetContacts_ProjectsSummaryList()
    {
        // Arrange
        var ownerId = PublicIdentityId.New();
        var peer1 = PublicIdentityId.New();
        var peer2 = PublicIdentityId.New();

        var contact1 = PeerContact.Rehydrate(
            ownerId, peer1, ContactNickname.Create("Bob", peer1),
            PeerTrustLevel.Verified, ContactState.Active,
            DateTimeOffset.UtcNow, lastSeenAtUtc: DateTimeOffset.UtcNow,
            primaryPublicKey: null);

        var contact2 = PeerContact.Rehydrate(
            ownerId, peer2, ContactNickname.Create("Charlie", peer2),
            PeerTrustLevel.Tofu, ContactState.PendingApproval,
            DateTimeOffset.UtcNow, lastSeenAtUtc: null,
            primaryPublicKey: null);

        await _repository.SaveAsync(contact1);
        await _repository.SaveAsync(contact2);

        // Act
        var summaries = await _queryService.GetContactSummariesAsync(ownerId);

        // Assert
        summaries.Should().HaveCount(2);
        summaries.Should().Contain(s => s.DisplayName == "Bob" && s.IsOnline);
        summaries.Should().Contain(s => s.DisplayName == "Charlie" && !s.IsOnline);
    }
}
