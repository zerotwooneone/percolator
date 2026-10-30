namespace Percolator.Infrastructure.Tests.Persistence.Relay;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;

[TestFixture]
public class SqliteRelayPreKeyRepositoryTests
{
    private SqliteTestDb _testDb = null!;
    private SqliteRelayPreKeyRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        _testDb = new SqliteTestDb();
        _repository = new SqliteRelayPreKeyRepository(_testDb.DbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _testDb.Dispose();
    }

    [Test]
    public async Task SaveAndConsumeBundle_DequeuesOneTimePreKeyAtomically()
    {
        // Arrange
        var ownerId = PublicIdentityId.New();
        var deviceId = DeviceId.Primary;
        var identityKey = IdentityKey.FromSpan(new byte[32]);
        var signedPreKey = DhPublicKey.FromSpan(new byte[32]);
        var signature = DeviceLinkProof.FromSpan(new byte[64]);

        var opk1 = (1u, DhPublicKey.FromSpan(new byte[32]));
        var opk2 = (2u, DhPublicKey.FromSpan(new byte[32]));

        var bundle = RelayHostedPreKeyBundle.Rehydrate(
            ownerId,
            deviceId,
            identityKey,
            signedPreKey,
            signature,
            new[] { opk1, opk2 });

        // Act - Save bundle
        await _repository.SaveAsync(bundle);

        var loaded = await _repository.GetAsync(ownerId, deviceId);
        loaded.Should().NotBeNull();
        loaded!.AvailableOneTimePreKeyCount.Should().Be(2);

        // Consume one OPK
        var consumed = loaded.ConsumeBundle();
        consumed.IsSuccess.Should().BeTrue();
        consumed.Value.OneTimePreKeyId.Should().Be(1u);
        loaded.AvailableOneTimePreKeyCount.Should().Be(1);

        // Re-save updated bundle
        await _repository.SaveAsync(loaded);

        // Assert - Reload in fresh context
        using var newContext = _testDb.CreateNewContext();
        var newRepo = new SqliteRelayPreKeyRepository(newContext);
        var reloaded = await newRepo.GetAsync(ownerId, deviceId);

        reloaded.Should().NotBeNull();
        reloaded!.AvailableOneTimePreKeyCount.Should().Be(1);

        // Consume second OPK
        var secondConsumed = reloaded.ConsumeBundle();
        secondConsumed.IsSuccess.Should().BeTrue();
        secondConsumed.Value.OneTimePreKeyId.Should().Be(2u);
        reloaded.AvailableOneTimePreKeyCount.Should().Be(0);
    }
}
