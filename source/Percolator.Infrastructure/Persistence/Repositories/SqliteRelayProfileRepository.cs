namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Persistence.Entities;

public sealed class SqliteRelayProfileRepository : IRelayProfileRepository
{
    private readonly PercolatorDbContext _dbContext;

    public SqliteRelayProfileRepository(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async ValueTask<RelayHostedProfileSlot?> GetSlotAsync(BlindedProfileLocator locator, CancellationToken ct = default)
    {
        var locatorBytes = locator.Span.ToArray();

        var entity = await _dbContext.RelayProfileSlots
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.BlindedLocator == locatorBytes, ct);

        if (entity == null)
        {
            return null;
        }

        var package = new ProfileCiphertextPackage(entity.Nonce, entity.Ciphertext);
        return new RelayHostedProfileSlot(
            locator,
            entity.WriteTokenHash,
            package,
            entity.Revision,
            entity.ExpiresAtUtc);
    }

    public async ValueTask<DomainResult> SaveSlotAsync(RelayHostedProfileSlot slot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(slot);

        var locatorBytes = slot.Id.Span.ToArray();

        var existing = await _dbContext.RelayProfileSlots
            .FirstOrDefaultAsync(s => s.BlindedLocator == locatorBytes, ct);

        if (existing == null)
        {
            var entity = new RelayProfileSlotEntity
            {
                BlindedLocator = locatorBytes,
                WriteTokenHash = (byte[])slot.WriteTokenHash.Clone(),
                Ciphertext = (byte[])slot.Package.Ciphertext.Clone(),
                Nonce = (byte[])slot.Package.Nonce.Clone(),
                Revision = slot.Revision,
                ExpiresAtUtc = slot.ExpiresAtUtc
            };
            await _dbContext.RelayProfileSlots.AddAsync(entity, ct);
        }
        else
        {
            if (!CryptographicOperations.FixedTimeEquals(existing.WriteTokenHash, slot.WriteTokenHash))
            {
                return DomainResult.Failure(new DomainError("UNAUTHORIZED_PROFILE_WRITE", "Invalid write token for profile slot."));
            }

            if (slot.Revision <= existing.Revision)
            {
                return DomainResult.Failure(new DomainError("STALE_PROFILE_REVISION", "New profile revision must be strictly greater than current revision."));
            }

            existing.Ciphertext = (byte[])slot.Package.Ciphertext.Clone();
            existing.Nonce = (byte[])slot.Package.Nonce.Clone();
            existing.Revision = slot.Revision;
            existing.ExpiresAtUtc = slot.ExpiresAtUtc;
        }

        await _dbContext.SaveChangesAsync(ct);
        return DomainResult.Success();
    }

    public async ValueTask<DomainResult> DeleteSlotAsync(BlindedProfileLocator locator, ProfileWriteToken writeToken, CancellationToken ct = default)
    {
        var locatorBytes = locator.Span.ToArray();

        var existing = await _dbContext.RelayProfileSlots
            .FirstOrDefaultAsync(s => s.BlindedLocator == locatorBytes, ct);

        if (existing == null)
        {
            return DomainResult.Success();
        }

        var expectedHash = SHA256.HashData(writeToken.Span);
        if (!CryptographicOperations.FixedTimeEquals(existing.WriteTokenHash, expectedHash))
        {
            return DomainResult.Failure(new DomainError("UNAUTHORIZED_PROFILE_DELETE", "Invalid write token for profile slot deletion."));
        }

        _dbContext.RelayProfileSlots.Remove(existing);
        await _dbContext.SaveChangesAsync(ct);
        return DomainResult.Success();
    }
}
