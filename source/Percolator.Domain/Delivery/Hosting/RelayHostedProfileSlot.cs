using System.Security.Cryptography;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Domain.Delivery.Hosting;

/// <summary>
/// Aggregate root representing an encrypted user profile slot hosted on a Relay.
/// Enforces write authorization via SHA-256 digest of ProfileWriteToken, monotonic revision updates,
/// exact uniform package size limits (1040 bytes), and TTL expiration.
/// Storing the digest of the write token adheres to zero-knowledge host principles:
/// the host never stores the bearer write token in plaintext.
/// </summary>
public sealed class RelayHostedProfileSlot : AggregateRoot<BlindedProfileLocator>
{
    public const int ExactPackageSizeBytes = 1040; // 1024 B padded payload + 16 B AEAD tag

    public override BlindedProfileLocator Id { get; }
    public byte[] WriteTokenHash { get; }
    public ProfileCiphertextPackage Package { get; private set; }
    public uint Revision { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public RelayHostedProfileSlot(
        BlindedProfileLocator locator,
        byte[] writeTokenHash,
        ProfileCiphertextPackage package,
        uint revision,
        DateTimeOffset expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(writeTokenHash);
        ArgumentNullException.ThrowIfNull(package.Ciphertext);
        ArgumentNullException.ThrowIfNull(package.Nonce);

        if (writeTokenHash.Length != 32)
        {
            throw new ArgumentException("WriteTokenHash must be exactly 32 bytes (SHA-256).", nameof(writeTokenHash));
        }

        if (package.Ciphertext.Length != ExactPackageSizeBytes)
        {
            throw new ArgumentException($"Package size {package.Ciphertext.Length} must be exactly {ExactPackageSizeBytes} bytes.", nameof(package));
        }

        Id = locator;
        WriteTokenHash = (byte[])writeTokenHash.Clone();
        Package = package;
        Revision = revision;
        ExpiresAtUtc = expiresAtUtc;
    }

    public static DomainResult<RelayHostedProfileSlot> Create(
        BlindedProfileLocator locator,
        ProfileWriteToken writeToken,
        ProfileCiphertextPackage package,
        uint revision,
        DateTimeOffset expiresAtUtc)
    {
        if (package.Ciphertext == null || package.Nonce == null)
        {
            return DomainResult<RelayHostedProfileSlot>.Failure(
                new DomainError("NULL_PROFILE_DATA", "Package ciphertext and nonce must not be null."));
        }

        if (package.Ciphertext.Length != ExactPackageSizeBytes)
        {
            return DomainResult<RelayHostedProfileSlot>.Failure(
                new DomainError("PACKAGE_SIZE_INVALID", $"Profile package size of {package.Ciphertext.Length} bytes must be exactly {ExactPackageSizeBytes} bytes."));
        }

        var writeTokenHash = SHA256.HashData(writeToken.Span);

        return DomainResult<RelayHostedProfileSlot>.Success(
            new RelayHostedProfileSlot(locator, writeTokenHash, package, revision, expiresAtUtc));
    }

    public DomainResult Update(
        ProfileWriteToken presentedToken,
        ProfileCiphertextPackage newPackage,
        uint newRevision,
        DateTimeOffset newExpiresAtUtc)
    {
        if (newPackage.Ciphertext == null || newPackage.Nonce == null)
        {
            return DomainResult.Failure(new DomainError("NULL_PACKAGE", "Package must not be null."));
        }

        if (newPackage.Ciphertext.Length != ExactPackageSizeBytes)
        {
            return DomainResult.Failure(
                new DomainError("PACKAGE_SIZE_INVALID", $"Profile package size of {newPackage.Ciphertext.Length} bytes must be exactly {ExactPackageSizeBytes} bytes."));
        }

        Span<byte> presentedHash = stackalloc byte[32];
        SHA256.HashData(presentedToken.Span, presentedHash);

        if (!CryptographicOperations.FixedTimeEquals(WriteTokenHash, presentedHash))
        {
            return DomainResult.Failure(new DomainError("UNAUTHORIZED_PROFILE_WRITE", "Invalid write token for profile slot."));
        }

        if (newRevision <= Revision)
        {
            return DomainResult.Failure(new DomainError("STALE_PROFILE_REVISION", "New profile revision must be strictly greater than current revision."));
        }

        Package = newPackage;
        Revision = newRevision;
        ExpiresAtUtc = newExpiresAtUtc;
        return DomainResult.Success();
    }

    public bool VerifyWriteToken(ProfileWriteToken presentedToken)
    {
        Span<byte> presentedHash = stackalloc byte[32];
        SHA256.HashData(presentedToken.Span, presentedHash);
        return CryptographicOperations.FixedTimeEquals(WriteTokenHash, presentedHash);
    }

    public DomainResult<ProfileCiphertextPackage> TryGet(IDateTimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (ExpiresAtUtc <= timeProvider.UtcNow)
        {
            return DomainResult<ProfileCiphertextPackage>.Failure(new DomainError("PROFILE_EXPIRED", "Profile slot has expired."));
        }

        return DomainResult<ProfileCiphertextPackage>.Success(Package);
    }
}
