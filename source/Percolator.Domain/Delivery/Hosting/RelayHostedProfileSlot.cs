using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Domain.Delivery.Hosting;

/// <summary>
/// Aggregate root representing an encrypted user profile slot hosted on a Relay.
/// Enforces write authorization via ProfileWriteToken, monotonic revision updates,
/// exact uniform package size limits (1040 bytes), and TTL expiration.
/// </summary>
public sealed class RelayHostedProfileSlot : AggregateRoot<BlindedProfileLocator>
{
    public const int ExactPackageSizeBytes = 1040; // 1024 B padded payload + 16 B AEAD tag

    public override BlindedProfileLocator Id { get; }
    public ProfileWriteToken WriteToken { get; }
    public ProfileCiphertextPackage Package { get; private set; }
    public uint Revision { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public RelayHostedProfileSlot(
        BlindedProfileLocator locator,
        ProfileWriteToken writeToken,
        ProfileCiphertextPackage package,
        uint revision,
        DateTimeOffset expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(writeToken);
        ArgumentNullException.ThrowIfNull(package.Ciphertext);
        ArgumentNullException.ThrowIfNull(package.Nonce);

        if (package.Ciphertext.Length != ExactPackageSizeBytes)
        {
            throw new ArgumentException($"Package size {package.Ciphertext.Length} must be exactly {ExactPackageSizeBytes} bytes.", nameof(package));
        }

        Id = locator;
        WriteToken = writeToken;
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
        if (locator == null || writeToken == null || package.Ciphertext == null || package.Nonce == null)
        {
            return DomainResult<RelayHostedProfileSlot>.Failure(
                new DomainError("NULL_PROFILE_DATA", "Locator, write token, and package must not be null."));
        }

        if (package.Ciphertext.Length != ExactPackageSizeBytes)
        {
            return DomainResult<RelayHostedProfileSlot>.Failure(
                new DomainError("PACKAGE_SIZE_INVALID", $"Profile package size of {package.Ciphertext.Length} bytes must be exactly {ExactPackageSizeBytes} bytes."));
        }

        return DomainResult<RelayHostedProfileSlot>.Success(
            new RelayHostedProfileSlot(locator, writeToken, package, revision, expiresAtUtc));
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

        if (presentedToken == null || !WriteToken.Equals(presentedToken))
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
