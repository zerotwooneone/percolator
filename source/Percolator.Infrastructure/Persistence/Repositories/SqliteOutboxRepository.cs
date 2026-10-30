namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Application2.Delivery;
using Percolator.Application2.Delivery.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence.Entities;

public sealed class SqliteOutboxRepository : IOutboxRepository
{
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

    public async Task<IReadOnlyList<OutboxJob>> GetPendingJobsForOwnerAsync(PublicIdentityId ownerId, CancellationToken ct = default)
    {
        var ownerGuid = ownerId.Value;
        var entities = await _dbContext.OpsOutboxJobs
            .AsNoTracking()
            .Where(j => j.OwnerIdentityId == ownerGuid && (OutboxStatus)j.Status == OutboxStatus.Pending)
            .OrderBy(j => j.NextAttemptAtUtc)
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
            .Take(limit)
            .ToListAsync(ct);

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

        var routeJson = SerializeRoute(job.Route);
        var ownerGuid = job.OwnerIdentityId.Value;
        var recipientGuid = job.RecipientIdentityId.HasValue ? job.RecipientIdentityId.Value.Value : (Guid?)null;

        if (existing == null)
        {
            var entity = new OpsOutboxJobEntity
            {
                Id = job.Id,
                OwnerIdentityId = ownerGuid,
                ChannelId = job.ChannelId.Value,
                RecipientId = recipientGuid,
                DeliveryRouteType = (int)job.Route.Type,
                DeliveryRouteJson = routeJson,
                Payload = payloadBytes,
                Status = (int)job.Status,
                RetryCount = job.RetryCount,
                MaxAttempts = job.MaxAttempts,
                NextAttemptAtUtc = job.NextAttemptAtUtc,
                CreatedAtUtc = job.CreatedAtUtc
            };
            await _dbContext.OpsOutboxJobs.AddAsync(entity, ct);
        }
        else
        {
            existing.OwnerIdentityId = ownerGuid;
            existing.ChannelId = job.ChannelId.Value;
            existing.RecipientId = recipientGuid;
            existing.DeliveryRouteType = (int)job.Route.Type;
            existing.DeliveryRouteJson = routeJson;
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
        var ownerId = new PublicIdentityId(entity.OwnerIdentityId);
        PublicIdentityId? recipientId = entity.RecipientId.HasValue
            ? new PublicIdentityId(entity.RecipientId.Value)
            : null;

        var route = DeserializeRoute(entity.DeliveryRouteType, entity.DeliveryRouteJson);

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

    private static string SerializeRoute(DeliveryRoute route)
    {
        var dto = new DeliveryRouteDto
        {
            Type = (int)route.Type,
            DirectEndpoint = route.DirectEndpoint?.ToString(),
            TargetToken = route.TargetToken.HasValue ? route.TargetToken.Value.Value.ToString() : null,
            RelayEndpoint = route.RelayEndpoint?.ToString(),
            TargetDeliveryToken = route.TargetDeliveryToken != null ? Convert.ToHexString(route.TargetDeliveryToken.Span) : null
        };
        return JsonSerializer.Serialize(dto);
    }

    private static DeliveryRoute DeserializeRoute(int routeType, string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return new DeliveryRoute((DeliveryRouteType)routeType, null, null);
        }

        try
        {
            var dto = JsonSerializer.Deserialize<DeliveryRouteDto>(json);
            if (dto == null) return new DeliveryRoute((DeliveryRouteType)routeType, null, null);

            var directUri = !string.IsNullOrEmpty(dto.DirectEndpoint) ? new Uri(dto.DirectEndpoint) : null;
            BlindedRoutingToken? targetToken = !string.IsNullOrEmpty(dto.TargetToken) && Guid.TryParse(dto.TargetToken, out var ttGuid)
                ? new BlindedRoutingToken(ttGuid)
                : null;
            RelayEndpoint? relayEndpoint = !string.IsNullOrEmpty(dto.RelayEndpoint)
                ? RelayEndpoint.Parse(dto.RelayEndpoint)
                : null;
            DeliveryToken? targetDeliveryToken = !string.IsNullOrEmpty(dto.TargetDeliveryToken)
                ? DeliveryToken.FromSpan(Convert.FromHexString(dto.TargetDeliveryToken))
                : null;

            return new DeliveryRoute(
                (DeliveryRouteType)dto.Type,
                directUri,
                targetToken,
                relayEndpoint,
                targetDeliveryToken);
        }
        catch
        {
            return new DeliveryRoute((DeliveryRouteType)routeType, null, null);
        }
    }

    private sealed class DeliveryRouteDto
    {
        public int Type { get; set; }
        public string? DirectEndpoint { get; set; }
        public string? TargetToken { get; set; }
        public string? RelayEndpoint { get; set; }
        public string? TargetDeliveryToken { get; set; }
    }
}
