namespace Percolator.Infrastructure.Tests.Persistence;

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Delivery.Model;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Common;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;

[TestFixture]
public sealed class SqliteRelayRegistrationRepositoryTests
{
    private SqliteTestDb _testDb = null!;

    [SetUp]
    public void SetUp()
    {
        _testDb = new SqliteTestDb();
    }

    [TearDown]
    public void TearDown()
    {
        _testDb.Dispose();
    }

    private static RelayRegistration NewRegistration(CertificatePin? pin = null)
    {
        return RelayRegistration.Create(
            Domain.Identities.PublicIdentityId.New(),
            RelayEndpoint.FromHostAndPort("127.0.0.1", 9000),
            pin,
            new SystemDateTimeProvider()).Value!;
    }

    private SqliteRelayRegistrationRepository NewRepoOnNewContext() =>
        new(_testDb.CreateNewContext());

    [Test]
    public async Task SaveAsync_ThenGetByIdAsync_RoundTripsRegistration()
    {
        var pinBytes = new byte[32];
        RandomNumberGenerator.Fill(pinBytes);
        var pin = CertificatePin.FromSpan(pinBytes);
        var registration = NewRegistration(pin);

        var writeRepo = new SqliteRelayRegistrationRepository(_testDb.DbContext);
        await writeRepo.SaveAsync(registration);

        var readRepo = NewRepoOnNewContext();
        var loaded = await readRepo.GetByIdAsync(registration.Id);

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(registration.Id);
        loaded.Endpoint.Value.Should().Be(registration.Endpoint.Value);
        loaded.PinnedCertificate.Should().NotBeNull();
        loaded.PinnedCertificate!.Span.ToArray().Should().Equal(pinBytes);
        loaded.CreatedAtUtc.Should().BeCloseTo(registration.CreatedAtUtc, TimeSpan.FromSeconds(1));
        loaded.Bindings.Should().BeEmpty();
    }

    [Test]
    public async Task GetByIdAsync_UnknownRelay_ReturnsNull()
    {
        var repo = new SqliteRelayRegistrationRepository(_testDb.DbContext);
        var loaded = await repo.GetByIdAsync(Domain.Identities.PublicIdentityId.New());
        loaded.Should().BeNull();
    }

    [Test]
    public async Task SaveAsync_LinkRotateUnlink_PersistsBindingLifecycle()
    {
        var registration = NewRegistration();
        var ownerId = Domain.Identities.PublicIdentityId.New();
        var time = new SystemDateTimeProvider();

        registration.LinkIdentity(ownerId, time).IsSuccess.Should().BeTrue();
        var linked = registration.Bindings.Single();

        var writeRepo = new SqliteRelayRegistrationRepository(_testDb.DbContext);
        await writeRepo.SaveAsync(registration);

        var readRepo = NewRepoOnNewContext();
        var reloaded = (await readRepo.GetByIdAsync(registration.Id))!;
        reloaded.Bindings.Should().HaveCount(1);
        var reloadedBinding = reloaded.Bindings.Single();
        reloadedBinding.OwnerIdentityId.Should().Be(ownerId);
        reloadedBinding.RoutingToken.ToByteArray().Should().Equal(linked.RoutingToken.ToByteArray());
        reloadedBinding.DrainToken.Span.ToArray().Should().Equal(linked.DrainToken.Span.ToArray());
        reloadedBinding.PreviousDrainToken.Should().BeNull();

        // Rotate drain token on the reloaded aggregate and persist.
        reloaded.RotateDrainToken(ownerId).IsSuccess.Should().BeTrue();
        var rotated = reloaded.Bindings.Single();
        await readRepo.SaveAsync(reloaded);

        var verifyRepo = NewRepoOnNewContext();
        var verified = (await verifyRepo.GetByIdAsync(registration.Id))!;
        var verifiedBinding = verified.Bindings.Single();
        verifiedBinding.DrainToken.Span.ToArray().Should().Equal(rotated.DrainToken.Span.ToArray());
        verifiedBinding.PreviousDrainToken.Should().NotBeNull();
        verifiedBinding.PreviousDrainToken!.Span.ToArray().Should().Equal(linked.DrainToken.Span.ToArray());

        // Unlink and persist — binding row must be removed.
        verified.UnlinkIdentity(ownerId);
        await verifyRepo.SaveAsync(verified);

        var finalRepo = NewRepoOnNewContext();
        var final = (await finalRepo.GetByIdAsync(registration.Id))!;
        final.Bindings.Should().BeEmpty();
    }

    [Test]
    public async Task GetByOwnerIdentityAsync_ReturnsOnlyMatchingRelays()
    {
        var ownerId = Domain.Identities.PublicIdentityId.New();
        var time = new SystemDateTimeProvider();

        var matching = NewRegistration();
        matching.LinkIdentity(ownerId, time);
        var unrelated = NewRegistration();
        unrelated.LinkIdentity(Domain.Identities.PublicIdentityId.New(), time);

        var writeRepo = new SqliteRelayRegistrationRepository(_testDb.DbContext);
        await writeRepo.SaveAsync(matching);
        await writeRepo.SaveAsync(unrelated);

        var readRepo = NewRepoOnNewContext();
        var results = await readRepo.GetByOwnerIdentityAsync(ownerId);

        results.Should().HaveCount(1);
        results.Single().Id.Should().Be(matching.Id);
    }

    [Test]
    public async Task GetAllAsync_ReturnsEverySavedRegistration()
    {
        var first = NewRegistration();
        var second = NewRegistration();

        var writeRepo = new SqliteRelayRegistrationRepository(_testDb.DbContext);
        await writeRepo.SaveAsync(first);
        await writeRepo.SaveAsync(second);

        var readRepo = NewRepoOnNewContext();
        var all = await readRepo.GetAllAsync();

        all.Select(r => r.Id).Should().BeEquivalentTo(new[] { first.Id, second.Id });
    }

    [Test]
    public async Task DeleteAsync_RemovesRegistrationAndBindings()
    {
        var registration = NewRegistration();
        registration.LinkIdentity(Domain.Identities.PublicIdentityId.New(), new SystemDateTimeProvider());

        var writeRepo = new SqliteRelayRegistrationRepository(_testDb.DbContext);
        await writeRepo.SaveAsync(registration);
        await writeRepo.DeleteAsync(registration.Id);

        var readRepo = NewRepoOnNewContext();
        (await readRepo.GetByIdAsync(registration.Id)).Should().BeNull();
        (await readRepo.GetByOwnerIdentityAsync(registration.Bindings.FirstOrDefault()?.OwnerIdentityId ?? Domain.Identities.PublicIdentityId.New()))
            .Should().BeEmpty();
    }
}
