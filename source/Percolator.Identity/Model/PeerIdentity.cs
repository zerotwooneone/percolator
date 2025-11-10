using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Percolator.Identity;

namespace Percolator.Identity.Model;

public enum TrustState
{
    Unknown = 0,
    Verified = 1,
    Distrusted = 2
}

public enum VerificationMethod
{
    Tofu = 0,
    OutOfBand = 1,
    Manual = 2,
    RemoteAttestation = 3
}

public sealed record VerificationRecord(
    byte[] Fingerprint,
    VerificationMethod Method,
    DateTimeOffset VerifiedAt,
    string? VerifiedBy,
    string? Note = null,
    DateTimeOffset? NotBefore = null,
    DateTimeOffset? ExpiresAt = null);

public sealed class IdentityKey
{
    public byte[] Spki { get; }
    public byte[] Fingerprint { get; }
    public DateTimeOffset NotBefore { get; }
    public DateTimeOffset ExpiresAt { get; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public IdentityKey(byte[] spki, DateTimeOffset notBefore, DateTimeOffset expiresAt)
    {
        if (spki == null || spki.Length == 0) throw new ArgumentException("spki required", nameof(spki));
        if (expiresAt <= notBefore) throw new ArgumentException("expiresAt must be after notBefore");
        Spki = spki;
        NotBefore = notBefore;
        ExpiresAt = expiresAt;
        Fingerprint = ComputeFingerprint(spki);
    }

    public bool IsActiveAt(DateTimeOffset when)
        => RevokedAt == null && when >= NotBefore && when < ExpiresAt;

    public void Revoke(DateTimeOffset when)
    {
        RevokedAt = when;
    }

    private static byte[] ComputeFingerprint(byte[] spki)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(spki);
    }
}

public sealed class PeerIdentity
{
    private readonly List<IdentityKey> _keys = new();
    private readonly List<VerificationRecord> _verifications = new();
    private readonly List<object> _domainEvents = new();

    public PeerId Id { get; }
    public DisplayName? DisplayName { get; private set; }
    public IReadOnlyList<IdentityKey> Keys => _keys;
    public IReadOnlyList<VerificationRecord> Verifications => _verifications;
    public TrustState TrustState { get; private set; } = TrustState.Unknown;
    public IReadOnlyCollection<object> DomainEvents => _domainEvents.AsReadOnly();
    public int Version { get; private set; }

    public PeerIdentity(PeerId id)
    {
        Id = id;
    }

    public void SetDisplayName(DisplayName name) => DisplayName = name;

    public void SetDisplayName(string name) => DisplayName = new DisplayName(name);

    public void AddKey(byte[] spki, DateTimeOffset notBefore, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        var newKey = new IdentityKey(spki, notBefore, expiresAt);
        // Reject overlapping active windows at 'now'
        var newActiveNow = newKey.IsActiveAt(now);
        if (newActiveNow && _keys.Any(k => k.IsActiveAt(now)))
            throw new InvalidOperationException("Overlapping active key windows are not allowed at the current time.");
        _keys.Add(newKey);
    }

    public IdentityKey? GetActiveKey(DateTimeOffset when)
        => _keys.FirstOrDefault(k => k.IsActiveAt(when));

    public IdentityKey? GetNextScheduledKey(DateTimeOffset when)
        => _keys.Where(k => k.NotBefore > when).OrderBy(k => k.NotBefore).FirstOrDefault();

    public void VerifyOutOfBand(byte[] expectedFingerprint, DateTimeOffset now, string? verifiedBy)
    {
        var active = GetActiveKey(now) ?? throw new InvalidOperationException("No active key to verify.");
        if (!active.Fingerprint.SequenceEqual(expectedFingerprint))
            throw new InvalidOperationException("Fingerprint mismatch.");
        _verifications.Add(new VerificationRecord(active.Fingerprint, VerificationMethod.OutOfBand, now, verifiedBy));
        TrustState = TrustState.Verified;
        _domainEvents.Add(new PeerVerifiedEvent(Id, active.Fingerprint, VerificationMethod.OutOfBand, now, verifiedBy));
    }

    public void VerifyTofu(DateTimeOffset now, string? verifiedBy = null)
    {
        var active = GetActiveKey(now) ?? throw new InvalidOperationException("No active key to verify (TOFU).");
        _verifications.Add(new VerificationRecord(active.Fingerprint, VerificationMethod.Tofu, now, verifiedBy));
        TrustState = TrustState.Verified;
        _domainEvents.Add(new PeerVerifiedEvent(Id, active.Fingerprint, VerificationMethod.Tofu, now, verifiedBy));
    }

    public TrustState TrustStateFor(DateTimeOffset when)
    {
        var active = GetActiveKey(when);
        if (active == null) return TrustState.Unknown;
        var hasVerification = _verifications.Any(v => v.Fingerprint.SequenceEqual(active.Fingerprint));
        return hasVerification ? TrustState.Verified : TrustState.Unknown;
    }

    public void Distrust(string reason, DateTimeOffset now)
    {
        TrustState = TrustState.Distrusted;
        _domainEvents.Add(new PeerDistrustedEvent(Id, reason, now));
    }

    // Apply version from persistence (used by repositories)
    public void SetVersionFromPersistence(int version) => Version = version;
}

public sealed record PeerVerifiedEvent(PeerId PeerId, byte[] Fingerprint, VerificationMethod Method, DateTimeOffset VerifiedAt, string? VerifiedBy);
public sealed record PeerDistrustedEvent(PeerId PeerId, string Reason, DateTimeOffset At);
