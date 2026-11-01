namespace Percolator.Infrastructure.Tests.Plugins.Discovery;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.Discovery.Features.PrivateRendezvous;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;

[TestFixture]
public sealed class SqlitePrivateRendezvousServiceTests
{
    private static readonly byte[] SharedSecret = new byte[32]
    {
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
        17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32
    };

    [Test]
    public async Task PublishAndResolve_MatchesBlindedLocatorAndEnforces100QueryCap()
    {
        using var testDb = new SqliteTestDb();
        var service = new SqlitePrivateRendezvousService(testDb.DbContext);

        var publisherId = PublicIdentityId.New();
        var epoch = new DiscoveryEpoch(20000);
        var route = new RelayRouteDescriptor("relay://primary.percolator.net:443");

        var locator = service.DeriveLocator(publisherId, SharedSecret, epoch);
        var encryptedRoute = EncryptedRendezvousRoute.Encrypt(route, locator, SharedSecret, epoch, publisherId);

        var publishResult = await service.PublishEncryptedRendezvousAsync(locator, encryptedRoute);
        publishResult.IsSuccess.Should().BeTrue();

        // 1. Resolve with batch including the published locator
        var locators = new List<BlindedLocator> { locator };
        var matches = await service.ResolveRendezvousAsync(locators);

        matches.Should().ContainSingle();
        matches[0].Locator.Should().Be(locator);

        var decrypted = matches[0].EncryptedRoute.Decrypt(locator, SharedSecret, epoch, publisherId);
        decrypted.IsSuccess.Should().BeTrue();
        decrypted.Value.Value.Should().Be("relay://primary.percolator.net:443");

        // 2. Enforce 100 query cap: pass 120 locators where the published locator is at index 105 (beyond cap)
        var batchBeyondCap = new List<BlindedLocator>();
        for (int i = 0; i < 105; i++)
        {
            byte[] dummy = new byte[32];
            RandomNumberGenerator.Fill(dummy);
            batchBeyondCap.Add(BlindedLocator.FromSpan(dummy).Value!);
        }
        batchBeyondCap.Add(locator); // Index 105

        var matchesBeyondCap = await service.ResolveRendezvousAsync(batchBeyondCap);
        matchesBeyondCap.Should().BeEmpty("locator placed at index 105 must not be evaluated because query is clamped to first 100");

        // Now place locator within first 100 (index 50) and have 120 locators total
        batchBeyondCap[50] = locator;
        var matchesWithinCap = await service.ResolveRendezvousAsync(batchBeyondCap);
        matchesWithinCap.Should().ContainSingle();
        matchesWithinCap[0].Locator.Should().Be(locator);
    }
}
