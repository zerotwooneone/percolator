using System;
using System.Threading.Tasks;
using NUnit.Framework;
using Percolator.Identity;
using Percolator.Identity.Model;

namespace Percolator.IdentityTests;

[TestFixture]
public class PeerIdentityRepositoryTests
{
    private static byte[] Bytes(params byte[] b) => b;

    [Test]
    public async Task Save_and_get_by_id_and_name_work()
    {
        IPeerIdentityRepository repo = new InMemoryPeerIdentityRepository();
        var id = new PeerIdentity(new PeerId(Guid.NewGuid()));
        id.SetDisplayName("Alice");
        var now = DateTimeOffset.UtcNow;
        id.AddKey(Bytes(1,2,3), now, now.AddDays(1), now);

        await repo.SaveAsync(id);

        var loaded = await repo.GetByIdAsync(id.Id);
        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.Id, Is.EqualTo(id.Id));

        var byName = await repo.GetByNameAsync(new DisplayName("Alice"));
        Assert.That(byName, Is.Not.Null);
        Assert.That(byName!.Id, Is.EqualTo(id.Id));
    }

    [Test]
    public async Task Find_by_public_key_hash_returns_identity()
    {
        IPeerIdentityRepository repo = new InMemoryPeerIdentityRepository();
        var id = new PeerIdentity(new PeerId(Guid.NewGuid()));
        var now = DateTimeOffset.UtcNow;
        id.AddKey(Bytes(9,9,9), now, now.AddDays(1), now);
        await repo.SaveAsync(id);

        var fp = id.GetActiveKey(now)!.Fingerprint;
        var loaded = await repo.FindByPublicKeyHashAsync(fp);
        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.Id, Is.EqualTo(id.Id));
    }

    [Test]
    public async Task Optimistic_concurrency_detects_version_mismatch()
    {
        IPeerIdentityRepository repo = new InMemoryPeerIdentityRepository();
        var id = new PeerIdentity(new PeerId(Guid.NewGuid()));
        var now = DateTimeOffset.UtcNow;
        id.AddKey(Bytes(1), now, now.AddDays(1), now);
        await repo.SaveAsync(id);
        var v1 = id.Version;

        // Load fresh copy
        var fresh = await repo.GetByIdAsync(id.Id);
        Assert.That(fresh, Is.Not.Null);
        // Simulate stale update by manually tampering version
        var stale = new PeerIdentity(id.Id);
        stale.AddKey(Bytes(2), now, now.AddDays(2), now);
        // Set same version as first save to simulate stale writer
        stale.SetVersionForTesting(v1);

        // First, a valid second save with fresh copy should succeed
        await repo.SaveAsync(fresh!);

        // Then stale save should fail due to version mismatch
        Assert.ThrowsAsync<InvalidOperationException>(async () => await repo.SaveAsync(stale));
    }
}
