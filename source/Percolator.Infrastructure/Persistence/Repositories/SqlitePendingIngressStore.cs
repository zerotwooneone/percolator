namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Infrastructure.Persistence.Entities;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

/// <summary>
/// SQLite-backed <see cref="IPendingIngressStore"/> over the ambient scoped
/// <see cref="PercolatorDbContext"/> (ops table, no encryption-at-rest beyond the
/// vault key — rows hold ciphertext only, per DEP-INFRA-VOLATILE-ZEROIZATION).
/// SequenceNumber is assigned at insert so replay preserves relay receive order.
/// </summary>
public sealed class SqlitePendingIngressStore : IPendingIngressStore
{
    private readonly PercolatorDbContext _dbContext;

    public SqlitePendingIngressStore(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<PendingIngressRecord> UpsertAsync(
        PublicIdentityId ownerId,
        QueuedMailboxEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var ownerGuid = ownerId.Value;
        var queueId = envelope.QueueId.Value;

        var existing = await _dbContext.OpsPendingIngressEnvelopes
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.OwnerIdentityId == ownerGuid && e.QueueId == queueId, ct)
            .ConfigureAwait(false);
        if (existing != null)
        {
            return ToRecord(existing);
        }

        var entity = new OpsPendingIngressEnvelopeEntity
        {
            OwnerIdentityId = ownerGuid,
            QueueId = queueId,
            RoutingToken = envelope.Envelope.RecipientToken.ToByteArray(),
            Ciphertext = envelope.Envelope.Ciphertext.ToArray(),
            EnqueuedAtUtc = envelope.Envelope.EnqueuedAtUtc,
            ExpiresAtUtc = envelope.Envelope.ExpiresAtUtc,
            Attempts = 0,
        };

        _dbContext.OpsPendingIngressEnvelopes.Add(entity);
        try
        {
            await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            // Lost a concurrent insert on the dedup unique index — the canonical row wins.
            _dbContext.Entry(entity).State = EntityState.Detached;
            existing = await _dbContext.OpsPendingIngressEnvelopes
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.OwnerIdentityId == ownerGuid && e.QueueId == queueId, ct)
                .ConfigureAwait(false);
            if (existing != null)
            {
                return ToRecord(existing);
            }
            throw;
        }

        return ToRecord(entity);
    }

    public async Task<IReadOnlyList<PendingIngressRecord>> ListByOwnerAsync(
        PublicIdentityId ownerId,
        CancellationToken ct = default)
    {
        var ownerGuid = ownerId.Value;
        var rows = await _dbContext.OpsPendingIngressEnvelopes
            .AsNoTracking()
            .Where(e => e.OwnerIdentityId == ownerGuid)
            .OrderBy(e => e.SequenceNumber)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.Select(ToRecord).ToList();
    }

    public async Task DeleteAsync(
        PublicIdentityId ownerId,
        MailboxQueueId queueId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(queueId);

        var ownerGuid = ownerId.Value;
        var entity = await _dbContext.OpsPendingIngressEnvelopes
            .FirstOrDefaultAsync(e => e.OwnerIdentityId == ownerGuid && e.QueueId == queueId.Value, ct)
            .ConfigureAwait(false);
        if (entity == null)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(entity.Ciphertext);
        _dbContext.OpsPendingIngressEnvelopes.Remove(entity);
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RecordTransientFailureAsync(
        PublicIdentityId ownerId,
        MailboxQueueId queueId,
        DomainError error,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(queueId);

        var ownerGuid = ownerId.Value;
        var entity = await _dbContext.OpsPendingIngressEnvelopes
            .FirstOrDefaultAsync(e => e.OwnerIdentityId == ownerGuid && e.QueueId == queueId.Value, ct)
            .ConfigureAwait(false);
        if (entity == null)
        {
            return;
        }

        entity.Attempts += 1;
        entity.LastErrorCode = error.Code;
        entity.LastErrorDescription = error.Description;
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static PendingIngressRecord ToRecord(OpsPendingIngressEnvelopeEntity entity)
    {
        var queueId = MailboxQueueId.Parse(entity.QueueId);
        // EnvelopeId is local metadata — a fresh one is generated on rehydration; the
        // spool's dedup key is QueueId, which persists.
        var envelope = new MailboxEnvelope(
            EnvelopeId.New(),
            BlindedRoutingToken.FromBytes(entity.RoutingToken),
            entity.Ciphertext,
            entity.EnqueuedAtUtc,
            entity.ExpiresAtUtc);
        var lastError = entity.LastErrorCode is null
            ? (DomainError?)null
            : new DomainError(entity.LastErrorCode, entity.LastErrorDescription ?? string.Empty);

        return new PendingIngressRecord(
            queueId,
            new QueuedMailboxEnvelope(queueId, envelope),
            entity.Attempts,
            lastError);
    }
}
