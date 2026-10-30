namespace Percolator.Infrastructure.Tests.Persistence.Relay;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;

[TestFixture]
public class SqliteRelayLedgerRepositoryTests
{
    private SqliteTestDb _testDb = null!;
    private SqliteRelayLedgerRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        _testDb = new SqliteTestDb();
        _repository = new SqliteRelayLedgerRepository(_testDb.DbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _testDb.Dispose();
    }

    [Test]
    public async Task SaveAndLoadLedger_PreservesEpochAndActiveRoutingTokens()
    {
        // Arrange
        var channelId = ChannelId.New();
        var relayId = PublicIdentityId.New();
        var epoch = new EpochNumber(1);
        var rosterBlob = EncryptedEntriesBlob.FromSpan(new byte[64]);
        var publicParams = ZkGroupPublicParams.FromSpan(new byte[32]);

        var token1 = BlindedRoutingToken.New();
        var token2 = BlindedRoutingToken.New();

        var ledger = RelayGroupLedger.Rehydrate(
            channelId,
            relayId,
            epoch,
            rosterBlob,
            new[] { token1, token2 },
            publicParams,
            DateTimeOffset.UtcNow);

        // Act - Save genesis ledger
        await _repository.SaveAsync(ledger);

        // Assert - Reload in fresh context
        using var newContext = _testDb.CreateNewContext();
        var newRepo = new SqliteRelayLedgerRepository(newContext);
        var loaded = await newRepo.GetByChannelIdAsync(channelId);

        loaded.Should().NotBeNull();
        loaded!.ChannelId.Should().Be(channelId);
        loaded.RelayIdentityId.Should().Be(relayId);
        loaded.CurrentEpoch.Value.Should().Be(1);
        loaded.ActiveRoutingTokens.Should().HaveCount(2);
        loaded.ActiveRoutingTokens.Should().Contain(token1);
        loaded.ActiveRoutingTokens.Should().Contain(token2);

        // Act - Mutate epoch and tokens, re-save
        var token3 = BlindedRoutingToken.New();
        var updatedLedger = RelayGroupLedger.Rehydrate(
            channelId,
            relayId,
            new EpochNumber(2),
            rosterBlob,
            new[] { token2, token3 }, // token1 removed, token3 added
            publicParams,
            DateTimeOffset.UtcNow);

        await newRepo.SaveAsync(updatedLedger);

        // Assert - Reload and verify token synchronization
        using var thirdContext = _testDb.CreateNewContext();
        var thirdRepo = new SqliteRelayLedgerRepository(thirdContext);
        var reloaded = await thirdRepo.GetByChannelIdAsync(channelId);

        reloaded.Should().NotBeNull();
        reloaded!.CurrentEpoch.Value.Should().Be(2);
        reloaded.ActiveRoutingTokens.Should().HaveCount(2);
        reloaded.ActiveRoutingTokens.Should().NotContain(token1);
        reloaded.ActiveRoutingTokens.Should().Contain(token2);
        reloaded.ActiveRoutingTokens.Should().Contain(token3);
    }
}
