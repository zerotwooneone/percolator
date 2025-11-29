using System;
using NUnit.Framework;
using Percolator.Identity;
using Percolator.Identity.Model;

namespace Percolator.IdentityTests;

[TestFixture]
public class SelfIdentityTests
{
    private static byte[] Bytes(params byte[] b) => b;

    [Test]
    public void Key_validity_windows_control_activation_over_time()
    {
        var now = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var id = new SelfIdentity(new SelfId(1));

        // Key active from now to +1 day
        id.AddKey(spki: Bytes(1,2,3), notBefore: now, expiresAt: now.AddDays(1), now: now);

        // Before window: not active
        Assert.That(id.GetActiveKey(now.AddMinutes(-1)), Is.Null);
        // During window: active
        Assert.That(id.GetActiveKey(now)!.Fingerprint, Is.Not.Null);
        Assert.That(id.GetActiveKey(now.AddHours(12))!.Fingerprint, Is.Not.Null);
        // After expiry: not active
        Assert.That(id.GetActiveKey(now.AddDays(2)), Is.Null);
    }

    [Test]
    public void Overlapping_active_windows_are_rejected()
    {
        var now = DateTimeOffset.UtcNow;
        var id = new SelfIdentity(new SelfId(2));
        id.AddKey(spki: Bytes(9), notBefore: now, expiresAt: now.AddDays(1), now: now);

        // Attempt to add a second key that would also be active at 'now'
        Assert.Throws<InvalidOperationException>(() =>
            id.AddKey(spki: Bytes(8), notBefore: now, expiresAt: now.AddDays(2), now: now));
    }

    [Test]
    public void TouchLastUsed_updates_timestamp()
    {
        var id = new SelfIdentity(new SelfId(3));
        var t1 = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var t2 = t1.AddHours(1);
        id.TouchLastUsed(t1);
        Assert.That(id.LastUsedUtc, Is.EqualTo(t1));
        id.TouchLastUsed(t2);
        Assert.That(id.LastUsedUtc, Is.EqualTo(t2));
    }

    [Test]
    public void GetNextScheduledKey_returns_future_key()
    {
        var now = DateTimeOffset.UtcNow;
        var id = new SelfIdentity(new SelfId(4));
        id.AddKey(Bytes(1), notBefore: now, expiresAt: now.AddDays(1), now: now);
        id.AddKey(Bytes(2), notBefore: now.AddDays(2), expiresAt: now.AddDays(3), now: now);

        var next = id.GetNextScheduledKey(now.AddDays(1));
        Assert.That(next, Is.Not.Null);
        Assert.That(next!.NotBefore, Is.EqualTo(now.AddDays(2)));
    }
}
