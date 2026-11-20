namespace Percolator.Infrastructure.Tests.Persistence.Queries;

using System;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Model;
using Percolator.Infrastructure.Persistence.Entities;
using Percolator.Infrastructure.Persistence.Queries;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;

[TestFixture]
public sealed class SqlPersonaRelayQueryServiceTests
{
    [Test]
    public async Task GetRelayLinksForPersonaAsync_ReturnsOnlyBindingsForTargetPersona()
    {
        using var testDb = new SqliteTestDb();
        var queryService = new SqlPersonaRelayQueryService(testDb.ContextFactory);

        var targetPersonaId = PublicIdentityId.New();
        var otherPersonaId = PublicIdentityId.New();

        var relay1Id = PublicIdentityId.New();
        var relay2Id = PublicIdentityId.New();

        // Seed 2 relays
        testDb.DbContext.RelayRegistrations.AddRange(
            new RelayRegistrationEntity
            {
                RelayIdentityId = relay1Id.Value,
                Endpoint = "percolator://relay1.example.com:5102",
                CreatedAtUtc = DateTimeOffset.UtcNow
            },
            new RelayRegistrationEntity
            {
                RelayIdentityId = relay2Id.Value,
                Endpoint = "percolator://relay2.example.com:5102",
                CreatedAtUtc = DateTimeOffset.UtcNow
            });

        // Seed bindings: target persona linked to relay1; other persona linked to relay2
        testDb.DbContext.RelayMailboxBindings.AddRange(
            new RelayMailboxBindingEntity
            {
                RelayIdentityId = relay1Id.Value,
                OwnerIdentityId = targetPersonaId.Value,
                RoutingToken = new byte[16],
                DrainToken = new byte[32],
                LinkedAtUtc = DateTimeOffset.UtcNow,
                Status = (int)RelayLinkStatus.Enabled
            },
            new RelayMailboxBindingEntity
            {
                RelayIdentityId = relay2Id.Value,
                OwnerIdentityId = otherPersonaId.Value,
                RoutingToken = new byte[16],
                DrainToken = new byte[32],
                LinkedAtUtc = DateTimeOffset.UtcNow,
                Status = (int)RelayLinkStatus.Enabled
            });

        await testDb.DbContext.SaveChangesAsync();

        var links = await queryService.GetRelayLinksForPersonaAsync(targetPersonaId);

        links.Should().HaveCount(1);
        links[0].RelayIdentityId.Should().Be(relay1Id);
        links[0].Endpoint.Value.Should().Be("percolator://relay1.example.com:5102");
        links[0].Status.Should().Be(RelayLinkStatus.Enabled);
    }

    [Test]
    public async Task GetRelayLinksForPersonaAsync_ProjectsCorrectRelayLinkStatus()
    {
        using var testDb = new SqliteTestDb();
        var queryService = new SqlPersonaRelayQueryService(testDb.ContextFactory);

        var personaId = PublicIdentityId.New();
        var relayId = PublicIdentityId.New();

        testDb.DbContext.RelayRegistrations.Add(new RelayRegistrationEntity
        {
            RelayIdentityId = relayId.Value,
            Endpoint = "percolator://disabled-relay.example.com:5102",
            CreatedAtUtc = DateTimeOffset.UtcNow
        });

        testDb.DbContext.RelayMailboxBindings.Add(new RelayMailboxBindingEntity
        {
            RelayIdentityId = relayId.Value,
            OwnerIdentityId = personaId.Value,
            RoutingToken = new byte[16],
            DrainToken = new byte[32],
            LinkedAtUtc = DateTimeOffset.UtcNow,
            Status = (int)RelayLinkStatus.Disabled
        });

        await testDb.DbContext.SaveChangesAsync();

        var links = await queryService.GetRelayLinksForPersonaAsync(personaId);

        links.Should().HaveCount(1);
        links[0].Status.Should().Be(RelayLinkStatus.Disabled);
    }

    [Test]
    public async Task GetRelayLinksForPersonaAsync_ReturnsEmptyWhenNoBindingsExist()
    {
        using var testDb = new SqliteTestDb();
        var queryService = new SqlPersonaRelayQueryService(testDb.ContextFactory);

        var unlinkedPersonaId = PublicIdentityId.New();

        var links = await queryService.GetRelayLinksForPersonaAsync(unlinkedPersonaId);

        links.Should().BeEmpty();
    }
}
