namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Persistence.Entities;

/// <summary>
/// SQLite-backed relay mailbox capability store. Persists routing-token → capability
/// associations, the authorized delivery-token ring (cap 10), and queued sealed envelopes
/// in the vault database. Live stream subscriptions are transient and stay in memory.
/// Secret-token comparisons always run via <see cref="CryptographicOperations.FixedTimeEquals"/>
/// after loading rows — never inside SQL.
/// </summary>
public sealed class SqliteRelayMailboxCapabilityStore : IRelayMailboxCapabilityStore
{
    private const int AuthorizedTokenRingCapacity = 10;

    private readonly IDbContextFactory<PercolatorDbContext> _contextFactory;
    private readonly ConcurrentDictionary<BlindedRoutingToken, List<ChannelWriter<RelayQueuedEnvelopeRecord>>> _subscribers = new();
    private readonly SemaphoreSlim _enqueueLock = new(1, 1);
    private readonly SemaphoreSlim _registerLock = new(1, 1);

    public SqliteRelayMailboxCapabilityStore(IDbContextFactory<PercolatorDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    public async ValueTask RegisterAsync(
        BlindedRoutingToken routingToken,
        DeliveryToken deliveryToken,
        DeliveryToken drainToken,
        CancellationToken ct = default)
    {
        var result = await RegisterVerifiedAsync(routingToken, deliveryToken, drainToken, null, ct)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            throw new InvalidOperationException(result.Error.Description);
        }
    }

    public async ValueTask<DomainResult> RegisterVerifiedAsync(
        BlindedRoutingToken routingToken,
        DeliveryToken deliveryToken,
        DeliveryToken drainToken,
        DeliveryToken? existingDrainToken = null,
        CancellationToken ct = default)
    {
        await _registerLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = _contextFactory.CreateDbContext();
            var routing = routingToken.ToByteArray();
            var capability = await db.RelayMailboxCapabilities
                .FirstOrDefaultAsync(c => c.RoutingToken == routing, ct)
                .ConfigureAwait(false);
            var isNew = capability == null;
            capability ??= new RelayMailboxCapabilityEntity { RoutingToken = routing };

            if (capability.DrainToken.Length > 0)
            {
                bool drainTokenMatches = drainToken.Length == capability.DrainToken.Length &&
                    CryptographicOperations.FixedTimeEquals(capability.DrainToken, drainToken.Span);

                bool existingDrainTokenMatches = existingDrainToken != null &&
                    existingDrainToken.Length == capability.DrainToken.Length &&
                    CryptographicOperations.FixedTimeEquals(capability.DrainToken, existingDrainToken.Span);

                if (!drainTokenMatches && !existingDrainTokenMatches)
                {
                    return DomainResult.Failure(new DomainError(
                        "MAILBOX_OWNERSHIP_MISMATCH", "Cannot overwrite existing mailbox without valid drain token."));
                }
            }

            capability.DeliveryToken = deliveryToken.ToArray();
            capability.DrainToken = drainToken.ToArray();
            if (isNew) db.RelayMailboxCapabilities.Add(capability);

            var authorizedTokens = await db.RelayMailboxAuthorizedTokens
                .Where(t => t.RoutingToken == routing)
                .OrderBy(t => t.Ordinal)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            // Keep the newest AuthorizedTokenRingCapacity tokens (the one being inserted counts toward the cap).
            var overflow = authorizedTokens.Count - (AuthorizedTokenRingCapacity - 1);
            if (overflow > 0)
            {
                db.RelayMailboxAuthorizedTokens.RemoveRange(authorizedTokens.Take(overflow));
            }

            var nextOrdinal = authorizedTokens.Count > 0 ? authorizedTokens.Max(t => t.Ordinal) + 1 : 0;
            db.RelayMailboxAuthorizedTokens.Add(new RelayMailboxAuthorizedTokenEntity
            {
                RoutingToken = routing,
                Ordinal = nextOrdinal,
                DeliveryToken = deliveryToken.ToArray()
            });

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return DomainResult.Success();
        }
        finally
        {
            _registerLock.Release();
        }
    }

    public async ValueTask<bool> VerifyDeliveryTokenAsync(
        BlindedRoutingToken routingToken,
        DeliveryToken deliveryToken,
        CancellationToken ct = default)
    {
        await using var db = _contextFactory.CreateDbContext();
        var routing = routingToken.ToByteArray();
        var capability = await db.RelayMailboxCapabilities
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.RoutingToken == routing, ct)
            .ConfigureAwait(false);

        if (capability == null)
        {
            return false;
        }

        if (capability.DeliveryToken.Length > 0 &&
            capability.DeliveryToken.Length == deliveryToken.Length &&
            CryptographicOperations.FixedTimeEquals(capability.DeliveryToken, deliveryToken.Span))
        {
            return true;
        }

        var authorized = await db.RelayMailboxAuthorizedTokens
            .AsNoTracking()
            .Where(t => t.RoutingToken == routing)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var token in authorized)
        {
            if (token.DeliveryToken.Length == deliveryToken.Length &&
                CryptographicOperations.FixedTimeEquals(token.DeliveryToken, deliveryToken.Span))
            {
                return true;
            }
        }

        return false;
    }

    public async ValueTask<bool> VerifyDrainTokenAsync(
        BlindedRoutingToken routingToken,
        DeliveryToken drainToken,
        CancellationToken ct = default)
    {
        await using var db = _contextFactory.CreateDbContext();
        var routing = routingToken.ToByteArray();
        var capability = await db.RelayMailboxCapabilities
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.RoutingToken == routing, ct)
            .ConfigureAwait(false);

        if (capability == null)
        {
            return false;
        }

        return capability.DrainToken.Length > 0 &&
            capability.DrainToken.Length == drainToken.Length &&
            CryptographicOperations.FixedTimeEquals(capability.DrainToken, drainToken.Span);
    }

    public async ValueTask<DeliveryToken?> GetDrainTokenAsync(
        BlindedRoutingToken routingToken,
        CancellationToken ct = default)
    {
        await using var db = _contextFactory.CreateDbContext();
        var routing = routingToken.ToByteArray();
        var capability = await db.RelayMailboxCapabilities
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.RoutingToken == routing, ct)
            .ConfigureAwait(false);

        if (capability == null || capability.DrainToken.Length == 0)
        {
            return null;
        }

        return DeliveryToken.FromSpan(capability.DrainToken);
    }

    public async ValueTask EnqueueEnvelopeAsync(
        BlindedRoutingToken routingToken,
        RelayQueuedEnvelopeRecord envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var routing = routingToken.ToByteArray();
        var entity = new RelayQueuedEnvelopeEntity
        {
            QueueId = envelope.QueueId.Value,
            RoutingToken = routing,
            EnvelopePayload = envelope.EnvelopePayload.ToArray(),
            EnqueuedAtUtc = envelope.EnqueuedAtUtc
        };

        await _enqueueLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = _contextFactory.CreateDbContext();
            var maxOrdinal = await db.RelayQueuedEnvelopes
                .Where(e => e.RoutingToken == routing)
                .Select(e => (long?)e.Ordinal)
                .MaxAsync(ct)
                .ConfigureAwait(false);
            entity.Ordinal = (maxOrdinal ?? -1) + 1;
            db.RelayQueuedEnvelopes.Add(entity);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _enqueueLock.Release();
        }

        NotifySubscribers(routingToken, envelope);
    }

    public async ValueTask<DomainResult> EnqueueEnvelopeAsync(
        BlindedRoutingToken routingToken,
        DeliveryToken deliveryToken,
        MailboxEnvelope envelope,
        long maxMailboxBytes,
        CancellationToken ct = default)
    {
        bool isAuthorized = await VerifyDeliveryTokenAsync(routingToken, deliveryToken, ct).ConfigureAwait(false);
        if (!isAuthorized)
        {
            return DomainResult.Failure(new DomainError("PERMISSION_DENIED", "Invalid delivery token capability."));
        }

        var routing = routingToken.ToByteArray();
        await _enqueueLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = _contextFactory.CreateDbContext();
            var existingEnvelopes = await db.RelayQueuedEnvelopes
                .AsNoTracking()
                .Where(e => e.RoutingToken == routing)
                .Select(e => e.EnvelopePayload.Length)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            long currentTotalBytes = existingEnvelopes.Sum(len => (long)len);
            if (currentTotalBytes + envelope.Ciphertext.Length > maxMailboxBytes)
            {
                return DomainResult.Failure(new DomainError("MAILBOX_QUOTA_EXCEEDED", "Envelope size exceeds relay mailbox quota."));
            }

            var record = new RelayQueuedEnvelopeRecord(
                MailboxQueueId.Parse(Guid.NewGuid().ToString("N")),
                envelope.Ciphertext.ToArray(),
                envelope.EnqueuedAtUtc);

            var entity = new RelayQueuedEnvelopeEntity
            {
                QueueId = record.QueueId.Value,
                RoutingToken = routing,
                EnvelopePayload = record.EnvelopePayload.ToArray(),
                EnqueuedAtUtc = record.EnqueuedAtUtc
            };

            db.RelayQueuedEnvelopes.Add(entity);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            NotifySubscribers(routingToken, record);
            return DomainResult.Success();
        }
        finally
        {
            _enqueueLock.Release();
        }
    }

    public async ValueTask<DomainResult> EnqueueAuthenticatedInitialAsync(
        BlindedRoutingToken routingToken,
        MailboxEnvelope envelope,
        long maxMailboxBytes,
        CancellationToken ct = default)
    {
        var routing = routingToken.ToByteArray();

        await _enqueueLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = _contextFactory.CreateDbContext();
            var exists = await db.RelayMailboxCapabilities
                .AsNoTracking()
                .AnyAsync(c => c.RoutingToken == routing, ct)
                .ConfigureAwait(false);

            if (!exists)
            {
                return DomainResult.Failure(new DomainError("MAILBOX_NOT_FOUND", "Recipient mailbox not found."));
            }

            var existingEnvelopes = await db.RelayQueuedEnvelopes
                .AsNoTracking()
                .Where(e => e.RoutingToken == routing)
                .Select(e => e.EnvelopePayload.Length)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            long currentTotalBytes = existingEnvelopes.Sum(len => (long)len);
            if (currentTotalBytes + envelope.Ciphertext.Length > maxMailboxBytes)
            {
                return DomainResult.Failure(new DomainError("MAILBOX_QUOTA_EXCEEDED", "Envelope size exceeds relay mailbox quota."));
            }

            var record = new RelayQueuedEnvelopeRecord(
                MailboxQueueId.Parse(Guid.NewGuid().ToString("N")),
                envelope.Ciphertext.ToArray(),
                envelope.EnqueuedAtUtc);

            var entity = new RelayQueuedEnvelopeEntity
            {
                QueueId = record.QueueId.Value,
                RoutingToken = routing,
                EnvelopePayload = record.EnvelopePayload.ToArray(),
                EnqueuedAtUtc = record.EnqueuedAtUtc
            };

            db.RelayQueuedEnvelopes.Add(entity);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            NotifySubscribers(routingToken, record);
            return DomainResult.Success();
        }
        finally
        {
            _enqueueLock.Release();
        }
    }

    public async ValueTask<IReadOnlyList<RelayQueuedEnvelopeRecord>> GetEnvelopesAsync(
        BlindedRoutingToken routingToken,
        CancellationToken ct = default)
    {
        await using var db = _contextFactory.CreateDbContext();
        var routing = routingToken.ToByteArray();
        var rows = await db.RelayQueuedEnvelopes
            .AsNoTracking()
            .Where(e => e.RoutingToken == routing)
            .OrderBy(e => e.Ordinal)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.Select(ToRecord).ToList();
    }

    public async ValueTask<int> PurgeMailboxAsync(
        BlindedRoutingToken routingToken,
        CancellationToken ct = default)
    {
        await using var db = _contextFactory.CreateDbContext();
        var routing = routingToken.ToByteArray();
        return await db.RelayQueuedEnvelopes
            .Where(e => e.RoutingToken == routing)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }

    public IDisposable SubscribeMailbox(
        BlindedRoutingToken routingToken,
        ChannelWriter<RelayQueuedEnvelopeRecord> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var writers = _subscribers.GetOrAdd(routingToken, _ => new List<ChannelWriter<RelayQueuedEnvelopeRecord>>());
        lock (writers)
        {
            writers.Add(writer);
        }

        return new Subscription(() =>
        {
            lock (writers)
            {
                writers.Remove(writer);
            }
        });
    }

    public async ValueTask<bool> AcknowledgeEnvelopeAsync(
        BlindedRoutingToken routingToken,
        MailboxQueueId queueId,
        CancellationToken ct = default)
    {
        await using var db = _contextFactory.CreateDbContext();
        var routing = routingToken.ToByteArray();
        var removed = await db.RelayQueuedEnvelopes
            .Where(e => e.QueueId == queueId.Value && e.RoutingToken == routing)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
        return removed > 0;
    }

    private void NotifySubscribers(BlindedRoutingToken routingToken, RelayQueuedEnvelopeRecord envelope)
    {
        if (!_subscribers.TryGetValue(routingToken, out var writers))
        {
            return;
        }

        ChannelWriter<RelayQueuedEnvelopeRecord>[] snapshot;
        lock (writers)
        {
            snapshot = writers.ToArray();
        }

        foreach (var writer in snapshot)
        {
            writer.TryWrite(envelope);
        }
    }

    private static RelayQueuedEnvelopeRecord ToRecord(RelayQueuedEnvelopeEntity entity) => new(
        MailboxQueueId.Parse(entity.QueueId),
        entity.EnvelopePayload,
        entity.EnqueuedAtUtc);

    private sealed class Subscription : IDisposable
    {
        private readonly Action _unsubscribe;
        private int _disposed;

        public Subscription(Action unsubscribe) => _unsubscribe = unsubscribe;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _unsubscribe();
            }
        }
    }
}
