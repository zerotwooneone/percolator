using Percolator.Application.Delivery;
using Percolator.Application.Delivery.Ports;

namespace Percolator.Infrastructure.Persistence.Repositories;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence.Entities;
public sealed class SqliteOutboxRepository : IOutboxRepository
{
    private static long _globalSequenceNumber = DateTimeOffset.UtcNow.Ticks;
    private readonly PercolatorDbContext _dbContext;

    public SqliteOutboxRepository(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }
    public async Task<OutboxJob?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _dbContext.OpsOutboxJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == id, ct);
        return entity != null ? MapEntityToDomain(entity) : null;
    }
    public async Task<IReadOnlyList<OutboxJob>> GetPendingJobsForOwnerAsync(Domain.Identities.PublicIdentityId ownerId, CancellationToken ct = default)
    {
        var ownerGuid = ownerId.Value;
        var entities = await _dbContext.OpsOutboxJobs
            .AsNoTracking()
            .Where(j => j.OwnerIdentityId == ownerGuid && (OutboxStatus)j.Status == OutboxStatus.Pending)
            .OrderBy(j => j.NextAttemptAtUtc)
            .ThenBy(j => j.SequenceNumber)
            .ToListAsync(ct);
        return entities.Select(MapEntityToDomain).ToList();
    }
    public async Task<IReadOnlyList<OutboxJob>> GetDuePendingJobsAsync(DateTimeOffset asOfUtc, int limit = 50, CancellationToken ct = default)
    {
        var pendingStatus = (int)OutboxStatus.Pending;
        var entities = await _dbContext.OpsOutboxJobs
            .AsNoTracking()
            .Where(j => j.Status == pendingStatus && j.NextAttemptAtUtc != null && j.NextAttemptAtUtc <= asOfUtc)
            .OrderBy(j => j.NextAttemptAtUtc)
            .ThenBy(j => j.SequenceNumber)
            .Take(limit)
            .ToListAsync(ct);
        return entities.Select(MapEntityToDomain).ToList();
    }
    public async Task<IReadOnlyList<OutboxJob>> ClaimDuePendingJobsAsync(int batchSize, string workerInstanceId, TimeSpan leaseDuration, CancellationToken ct = default)
    {
        if (batchSize <= 0) return Array.Empty<OutboxJob>();
        var now = DateTimeOffset.UtcNow;
        var pendingStatus = (int)OutboxStatus.Pending;
        var inFlightStatus = (int)OutboxStatus.InFlight;

        await using var tx = await _dbContext.Database.BeginTransactionAsync(ct);
        var entities = await _dbContext.OpsOutboxJobs
            .Where(j => (j.Status == pendingStatus && j.NextAttemptAtUtc != null && j.NextAttemptAtUtc <= now)
                     || (j.Status == inFlightStatus && j.NextAttemptAtUtc != null && j.NextAttemptAtUtc <= now))
            .OrderBy(j => j.NextAttemptAtUtc)
            .ThenBy(j => j.SequenceNumber)
            .Take(batchSize)
            .ToListAsync(ct);

        if (entities.Count == 0)
        {
            await tx.RollbackAsync(ct);
            return Array.Empty<OutboxJob>();
        }

        var leaseExpiry = now.Add(leaseDuration);
        foreach (var entity in entities)
        {
            entity.Status = inFlightStatus;
            entity.NextAttemptAtUtc = leaseExpiry;
        }

        await _dbContext.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return entities.Select(MapEntityToDomain).ToList();
    }
    public async Task SaveAsync(OutboxJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var existing = await _dbContext.OpsOutboxJobs
            .FirstOrDefaultAsync(j => j.Id == job.Id, ct);
        // When delivered, scrub the plaintext payload from SQLite storage
        var payloadBytes = job.Status == OutboxStatus.Delivered
            ? Array.Empty<byte>()
            : job.Payload.ToArray();
        var ownerGuid = job.OwnerIdentityId.Value;
        var recipientGuid = job.RecipientIdentityId.HasValue ? job.RecipientIdentityId.Value.Value : (Guid?)null;
        var targetEndpoint = job.Route.RelayEndpoint?.ToString() ?? string.Empty;
        var targetRouting = job.Route.TargetToken.ToByteArray();
        var targetDelivery = job.Route.TargetDeliveryToken?.Span.ToArray();

        bool isNew = existing == null;
        if (isNew)
        {
            var entity = new OpsOutboxJobEntity
            {
                Id = job.Id,
                SequenceNumber = Interlocked.Increment(ref _globalSequenceNumber),
                OwnerIdentityId = ownerGuid,
                ChannelId = job.ChannelId.Value,
                RecipientId = recipientGuid,
                DeliveryRouteType = (int)job.Route.Type,
                TargetRelayEndpoint = targetEndpoint,
                TargetRoutingToken = targetRouting,
                TargetDeliveryToken = targetDelivery,
                Payload = payloadBytes,
                Status = (int)job.Status,
                RetryCount = job.RetryCount,
                MaxAttempts = job.MaxAttempts,
                NextAttemptAtUtc = job.NextAttemptAtUtc,
                CreatedAtUtc = job.CreatedAtUtc
            };
            await _dbContext.OpsOutboxJobs.AddAsync(entity, ct);
        }
        else if (existing != null)
        {
            existing.OwnerIdentityId = ownerGuid;
            existing.ChannelId = job.ChannelId.Value;
            existing.RecipientId = recipientGuid;
            existing.DeliveryRouteType = (int)job.Route.Type;
            existing.TargetRelayEndpoint = targetEndpoint;
            existing.TargetRoutingToken = targetRouting;
            existing.TargetDeliveryToken = targetDelivery;
            existing.Payload = payloadBytes;
            existing.Status = (int)job.Status;
            existing.RetryCount = job.RetryCount;
            existing.NextAttemptAtUtc = job.NextAttemptAtUtc;
        }
        await _dbContext.SaveChangesAsync(ct);
    }
    private static OutboxJob MapEntityToDomain(OpsOutboxJobEntity entity)
    {
        var channelId = new ChannelId(entity.ChannelId);
        var ownerId = new Domain.Identities.PublicIdentityId(entity.OwnerIdentityId);
        Domain.Identities.PublicIdentityId? recipientId = entity.RecipientId.HasValue
            ? new Domain.Identities.PublicIdentityId(entity.RecipientId.Value)
            : null;
        var route = (DeliveryRouteType)entity.DeliveryRouteType switch
        {
            DeliveryRouteType.RelayedOneToOne => new DeliveryRoute(
                DeliveryRouteType.RelayedOneToOne,
                BlindedRoutingToken.FromBytes(entity.TargetRoutingToken),
                RelayEndpoint.Parse(entity.TargetRelayEndpoint),
                entity.TargetDeliveryToken != null ? DeliveryToken.FromBytes(entity.TargetDeliveryToken) : null),
            DeliveryRouteType.RelayedGroup => new DeliveryRoute(
                DeliveryRouteType.RelayedGroup,
                BlindedRoutingToken.FromBytes(entity.TargetRoutingToken),
                RelayEndpoint.Parse(entity.TargetRelayEndpoint),
                entity.TargetDeliveryToken != null ? DeliveryToken.FromBytes(entity.TargetDeliveryToken) : null),
            _ => throw new InvalidOperationException($"Unsupported persistent delivery route type: {entity.DeliveryRouteType}")
        };
        return OutboxJob.Rehydrate(
            entity.Id,
            channelId,
            ownerId,
            recipientId,
            route,
            entity.Payload,
            (OutboxStatus)entity.Status,
            entity.RetryCount,
            entity.MaxAttempts,
            entity.CreatedAtUtc,
            entity.NextAttemptAtUtc);
    }
}
