namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Persistence.Entities;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Transport;

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
    private readonly ConcurrentDictionary<string, List<ChannelWriter<QueuedSealedEnvelopeProto>>> _subscribers = new();
    private readonly SemaphoreSlim _enqueueLock = new(1, 1);

    public SqliteRelayMailboxCapabilityStore(IDbContextFactory<PercolatorDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    private static string ToHexKey(ReadOnlyMemory<byte> token) => Convert.ToHexString(token.Span).ToLowerInvariant();

    public async ValueTask RegisterAsync(
        ReadOnlyMemory<byte> routingToken,
        ReadOnlyMemory<byte> deliveryToken,
        ReadOnlyMemory<byte> drainToken,
        PublicIdentityId? ownerId = null,
        DeviceId? deviceId = null,
        CancellationToken ct = default)
    {
        var result = await RegisterVerifiedAsync(routingToken, deliveryToken, drainToken, default, ownerId, deviceId, ct)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            throw new InvalidOperationException(result.Error.Description);
        }
    }

    public async ValueTask<DomainResult> RegisterVerifiedAsync(
        ReadOnlyMemory<byte> routingToken,
        ReadOnlyMemory<byte> deliveryToken,
        ReadOnlyMemory<byte> drainToken,
        ReadOnlyMemory<byte> existingDrainToken = default,
        PublicIdentityId? ownerId = null,
        DeviceId? deviceId = null,
        CancellationToken ct = default)
    {
        await using var db = _contextFactory.CreateDbContext();
        var routing = routingToken.ToArray();
        var capability = await db.RelayMailboxCapabilities
            .FirstOrDefaultAsync(c => c.RoutingToken == routing, ct)
            .ConfigureAwait(false);
        var isNew = capability == null;
        capability ??= new RelayMailboxCapabilityEntity { RoutingToken = routing };

        if (capability.DrainToken.Length > 0)
        {
            bool drainTokenMatches = drainToken.Length == capability.DrainToken.Length &&
                CryptographicOperations.FixedTimeEquals(capability.DrainToken, drainToken.Span);

            bool existingDrainTokenMatches = !existingDrainToken.IsEmpty &&
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
        if (ownerId.HasValue) capability.OwnerIdentityId = ownerId.Value.Value;
        if (deviceId.HasValue) capability.DeviceId = (int)deviceId.Value.Value;
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

    public async ValueTask AssociateOwnerAsync(
        ReadOnlyMemory<byte> routingToken,
        PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken ct = default)
    {
        await using var db = _contextFactory.CreateDbContext();
        var routing = routingToken.ToArray();
        var capability = await db.RelayMailboxCapabilities
            .FirstOrDefaultAsync(c => c.RoutingToken == routing, ct)
            .ConfigureAwait(false);

        if (capability == null)
        {
            capability = new RelayMailboxCapabilityEntity { RoutingToken = routing };
            db.RelayMailboxCapabilities.Add(capability);
        }

        capability.OwnerIdentityId = ownerId.Value;
        capability.DeviceId = (int)deviceId.Value;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<(PublicIdentityId? OwnerId, DeviceId? DeviceId)> GetOwnerAsync(
        ReadOnlyMemory<byte> routingToken,
        CancellationToken ct = default)
    {
        await using var db = _contextFactory.CreateDbContext();
        var routing = routingToken.ToArray();
        var capability = await db.RelayMailboxCapabilities
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.RoutingToken == routing, ct)
            .ConfigureAwait(false);

        if (capability == null)
        {
            return (null, null);
        }

        return (
            capability.OwnerIdentityId.HasValue ? new PublicIdentityId(capability.OwnerIdentityId.Value) : (PublicIdentityId?)null,
            capability.DeviceId.HasValue ? new DeviceId((uint)capability.DeviceId.Value) : (DeviceId?)null);
    }

    public async ValueTask<(byte[] RoutingToken, byte[] DeliveryToken)?> GetMailboxTokensByOwnerAsync(
        PublicIdentityId ownerId,
        CancellationToken ct = default)
    {
        await using var db = _contextFactory.CreateDbContext();
        var capability = await db.RelayMailboxCapabilities
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.OwnerIdentityId == ownerId.Value, ct)
            .ConfigureAwait(false);

        return capability == null ? null : (capability.RoutingToken, capability.DeliveryToken);
    }

    public async ValueTask<bool> VerifyDeliveryTokenAsync(
        ReadOnlyMemory<byte> routingToken,
        ReadOnlyMemory<byte> deliveryToken,
        CancellationToken ct = default)
    {
        await using var db = _contextFactory.CreateDbContext();
        var routing = routingToken.ToArray();
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
        ReadOnlyMemory<byte> routingToken,
        ReadOnlyMemory<byte> drainToken,
        CancellationToken ct = default)
    {
        await using var db = _contextFactory.CreateDbContext();
        var routing = routingToken.ToArray();
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

    public async ValueTask<ReadOnlyMemory<byte>?> GetDrainTokenAsync(
        ReadOnlyMemory<byte> routingToken,
        CancellationToken ct = default)
    {
        await using var db = _contextFactory.CreateDbContext();
        var routing = routingToken.ToArray();
        var capability = await db.RelayMailboxCapabilities
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.RoutingToken == routing, ct)
            .ConfigureAwait(false);

        if (capability == null || capability.DrainToken.Length == 0)
        {
            return null;
        }

        return capability.DrainToken;
    }

    public async ValueTask EnqueueEnvelopeAsync(
        ReadOnlyMemory<byte> routingToken,
        QueuedSealedEnvelopeProto envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var routing = routingToken.ToArray();
        var entity = new RelayQueuedEnvelopeEntity
        {
            QueueId = envelope.QueueId,
            RoutingToken = routing,
            EnvelopePayload = envelope.EnvelopePayload.ToByteArray(),
            EnqueuedAtUtc = envelope.EnqueuedAtUtc?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow
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

    public async ValueTask<IReadOnlyList<QueuedSealedEnvelopeProto>> GetEnvelopesAsync(
        ReadOnlyMemory<byte> routingToken,
        CancellationToken ct = default)
    {
        await using var db = _contextFactory.CreateDbContext();
        var routing = routingToken.ToArray();
        var rows = await db.RelayQueuedEnvelopes
            .AsNoTracking()
            .Where(e => e.RoutingToken == routing)
            .OrderBy(e => e.Ordinal)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.Select(ToProto).ToList();
    }

    public async ValueTask<IReadOnlyList<QueuedSealedEnvelopeProto>> DrainEnvelopesAsync(
        ReadOnlyMemory<byte> routingToken,
        CancellationToken ct = default)
    {
        await using var db = _contextFactory.CreateDbContext();
        var routing = routingToken.ToArray();
        var rows = await db.RelayQueuedEnvelopes
            .Where(e => e.RoutingToken == routing)
            .OrderBy(e => e.Ordinal)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var drained = rows.Select(ToProto).ToList();
        db.RelayQueuedEnvelopes.RemoveRange(rows);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return drained;
    }

    public IDisposable SubscribeMailbox(
        ReadOnlyMemory<byte> routingToken,
        ChannelWriter<QueuedSealedEnvelopeProto> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var key = ToHexKey(routingToken);
        var writers = _subscribers.GetOrAdd(key, _ => new List<ChannelWriter<QueuedSealedEnvelopeProto>>());
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
        ReadOnlyMemory<byte> routingToken,
        string queueId,
        CancellationToken ct = default)
    {
        await using var db = _contextFactory.CreateDbContext();
        var routing = routingToken.ToArray();
        var removed = await db.RelayQueuedEnvelopes
            .Where(e => e.QueueId == queueId && e.RoutingToken == routing)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
        return removed > 0;
    }

    private void NotifySubscribers(ReadOnlyMemory<byte> routingToken, QueuedSealedEnvelopeProto envelope)
    {
        var key = ToHexKey(routingToken);
        if (!_subscribers.TryGetValue(key, out var writers))
        {
            return;
        }

        ChannelWriter<QueuedSealedEnvelopeProto>[] snapshot;
        lock (writers)
        {
            snapshot = writers.ToArray();
        }

        foreach (var writer in snapshot)
        {
            writer.TryWrite(envelope);
        }
    }

    private static QueuedSealedEnvelopeProto ToProto(RelayQueuedEnvelopeEntity entity) => new()
    {
        QueueId = entity.QueueId,
        EnvelopePayload = ByteString.CopyFrom(entity.EnvelopePayload),
        EnqueuedAtUtc = Timestamp.FromDateTimeOffset(entity.EnqueuedAtUtc)
    };

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
