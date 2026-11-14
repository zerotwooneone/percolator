namespace Percolator.Infrastructure.Tests.Persistence.Vault;

using System;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Application.Ports;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

[TestFixture]
public sealed class SqliteVaultSettingsStoreTests
{
    private SqliteTestDb _db = null!;
    private SqliteVaultSettingsStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new SqliteTestDb();
        _store = new SqliteVaultSettingsStore(_db.ContextFactory);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    [Test]
    public async Task GetAsync_WhenMissing_ReturnsNull()
    {
        var result = await _store.GetAsync(new PublicIdentityId(Guid.NewGuid()));
        result.Should().BeNull();
    }

    [Test]
    public async Task SaveAsync_ThenGet_RoundTripsAllFields()
    {
        var id = new PublicIdentityId(Guid.NewGuid());
        var settings = new IdentityLaunchSettings(PreferredPort: 5100, LastBoundPort: 5101, AutoStart: true, SidebarOrder: 2);

        await _store.SaveAsync(id, settings);
        var loaded = await _store.GetAsync(id);

        loaded.Should().Be(settings);
    }

    [Test]
    public async Task SaveAsync_SecondSave_UpsertsSameRow()
    {
        var id = new PublicIdentityId(Guid.NewGuid());
        await _store.SaveAsync(id, new IdentityLaunchSettings(5100, null, false, 0));
        await _store.SaveAsync(id, new IdentityLaunchSettings(5200, 5201, true, 3));

        var all = await _store.GetAllAsync();
        all.Should().ContainSingle();
        all[0].IdentityId.Should().Be(id);
        all[0].Settings.PreferredPort.Should().Be(5200);
        all[0].Settings.LastBoundPort.Should().Be(5201);
        all[0].Settings.AutoStart.Should().BeTrue();
        all[0].Settings.SidebarOrder.Should().Be(3);
    }

    [Test]
    public async Task GetAllAsync_ReturnsRowsOrderedBySidebarOrder()
    {
        var first = new PublicIdentityId(Guid.NewGuid());
        var second = new PublicIdentityId(Guid.NewGuid());
        var third = new PublicIdentityId(Guid.NewGuid());
        await _store.SaveAsync(second, new IdentityLaunchSettings(5101, null, false, 2));
        await _store.SaveAsync(third, new IdentityLaunchSettings(5102, null, false, 7));
        await _store.SaveAsync(first, new IdentityLaunchSettings(5100, null, false, 0));

        var all = await _store.GetAllAsync();

        all.Select(r => r.IdentityId).Should().Equal(first, second, third);
    }

    [Test]
    public async Task DeleteAsync_RemovesRow_AndIsSafeWhenMissing()
    {
        var id = new PublicIdentityId(Guid.NewGuid());
        await _store.SaveAsync(id, new IdentityLaunchSettings(5100, null, false, 0));

        await _store.DeleteAsync(id);
        (await _store.GetAsync(id)).Should().BeNull();

        // No-op on a missing row — no throw.
        await _store.DeleteAsync(id);
    }

    [Test]
    public async Task GetAppSettingAsync_WhenMissing_ReturnsNull()
    {
        (await _store.GetAppSettingAsync(VaultSettingKeys.OnboardingCompleted)).Should().BeNull();
    }

    [Test]
    public async Task SetAppSettingAsync_UpsertsValue_Idempotently()
    {
        await _store.SetAppSettingAsync(VaultSettingKeys.OnboardingCompleted, "true");
        await _store.SetAppSettingAsync(VaultSettingKeys.OnboardingCompleted, "true");
        await _store.SetAppSettingAsync(VaultSettingKeys.LastActiveIdentityId, Guid.NewGuid().ToString("N"));

        (await _store.GetAppSettingAsync(VaultSettingKeys.OnboardingCompleted)).Should().Be("true");
        (await _store.GetAppSettingAsync(VaultSettingKeys.LastActiveIdentityId)).Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task SetAppSettingAsync_SecondWriteOverwrites()
    {
        const string key = "Custom.Key";
        await _store.SetAppSettingAsync(key, "first");
        await _store.SetAppSettingAsync(key, "second");

        (await _store.GetAppSettingAsync(key)).Should().Be("second");
    }

    [Test]
    public void SaveAsync_PreferredPortOutOfRange_Throws()
    {
        var id = new PublicIdentityId(Guid.NewGuid());
        var act = async () => await _store.SaveAsync(id, new IdentityLaunchSettings(70000, null, false, 0));
        act.Should().ThrowAsync<ArgumentOutOfRangeException>(
            "ports are range-validated at the store boundary");

        var actZero = async () => await _store.SaveAsync(id, new IdentityLaunchSettings(0, null, false, 0));
        actZero.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Test]
    public void SaveAsync_LastBoundPortOutOfRange_Throws()
    {
        var id = new PublicIdentityId(Guid.NewGuid());
        var act = async () => await _store.SaveAsync(id, new IdentityLaunchSettings(5100, -1, false, 0));
        act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
