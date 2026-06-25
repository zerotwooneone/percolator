using Percolator.Identity;
using Percolator.Identity.Model;

namespace Percolator.IdentityTests;

[TestFixture]
public class PeerIdentityTests
{
    private static byte[] Bytes(params byte[] b) => b;

    [Test]
    public void Key_validity_windows_control_activation_over_time()
    {
        var now = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var id = new PeerIdentity(new PeerId(1));

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
        var id = new PeerIdentity(new PeerId(1));
        id.AddKey(spki: Bytes(9), notBefore: now, expiresAt: now.AddDays(1), now: now);

        // Attempt to add a second key that would also be active at 'now'
        Assert.Throws<InvalidOperationException>(() =>
            id.AddKey(spki: Bytes(8), notBefore: now, expiresAt: now.AddDays(2), now: now));
    }

    [Test]
    public void Rotation_can_be_scheduled_in_future_without_overlap()
    {
        var now = DateTimeOffset.UtcNow;
        var id = new PeerIdentity(new PeerId(1));
        id.AddKey(spki: Bytes(1), notBefore: now, expiresAt: now.AddDays(1), now: now);

        // Next key starts right when previous expires
        id.AddKey(spki: Bytes(2), notBefore: now.AddDays(1), expiresAt: now.AddDays(2), now: now);

        Assert.That(id.GetActiveKey(now)!.Fingerprint, Is.Not.Null);
        Assert.That(id.GetActiveKey(now.AddDays(1).AddSeconds(-1))!.Fingerprint, Is.Not.Null);
        Assert.That(id.GetActiveKey(now.AddDays(1)), Is.Not.Null); // switches at boundary
    }

    [Test]
    public void Verify_OutOfBand_marks_trusted_for_active_fingerprint_only()
    {
        var now = DateTimeOffset.UtcNow;
        var id = new PeerIdentity(new PeerId(1));
        id.AddKey(spki: Bytes(1,1,1), notBefore: now, expiresAt: now.AddDays(1), now: now);
        var active = id.GetActiveKey(now)!;

        id.VerifyOutOfBand(expectedFingerprint: active.Fingerprint, now: now, verifiedBy: "tester");

        Assert.That(id.TrustState, Is.EqualTo(TrustState.Verified));
        Assert.That(id.Verifications.Count, Is.EqualTo(1));
        Assert.That(id.Verifications[0].Fingerprint, Is.EqualTo(active.Fingerprint));

        // After rotation, trust should not auto-carry to new key
        id.AddKey(spki: Bytes(2,2,2), notBefore: now.AddDays(1), expiresAt: now.AddDays(2), now: now);
        Assert.That(id.TrustStateFor(now.AddDays(2)), Is.EqualTo(TrustState.Unknown));
    }

    [Test]
    public void Verify_rejects_when_no_active_key_or_mismatch()
    {
        var now = DateTimeOffset.UtcNow;
        var id = new PeerIdentity(new PeerId(1));
        // No keys
        Assert.Throws<InvalidOperationException>(() => id.VerifyOutOfBand(new byte[32], now, null));

        // Add a future-dated key; still no active
        id.AddKey(Bytes(7,7,7), notBefore: now.AddMinutes(10), expiresAt: now.AddDays(1), now: now);
        Assert.Throws<InvalidOperationException>(() => id.VerifyOutOfBand(new byte[32], now, null));

        // When active but fingerprint mismatch
        var later = now.AddMinutes(20);
        var active = id.GetActiveKey(later)!;
        var wrongFp = new byte[active.Fingerprint.Length];
        Assert.Throws<InvalidOperationException>(() => id.VerifyOutOfBand(wrongFp, later, null));
    }

    [Test]
    public void Distrust_moves_state_out_of_verified()
    {
        var now = DateTimeOffset.UtcNow;
        var id = new PeerIdentity(new PeerId(1));
        id.AddKey(Bytes(1), now, now.AddDays(1), now);
        var active = id.GetActiveKey(now)!;
        id.VerifyOutOfBand(active.Fingerprint, now, null);

        id.Distrust("user request", now.AddMinutes(1));
        Assert.That(id.TrustState, Is.EqualTo(TrustState.Distrusted));
    }
}
