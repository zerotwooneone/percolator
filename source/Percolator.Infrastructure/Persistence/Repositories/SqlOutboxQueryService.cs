namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Application2.Delivery;
using Percolator.Application2.Delivery.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

public sealed class SqlOutboxQueryService : IOutboxQueryService
{
    private readonly PercolatorDbContext _dbContext;

    public SqlOutboxQueryService(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<IReadOnlyList<OutboxJobSummaryReadModel>> GetPendingJobsAsync(int limit = 50, CancellationToken ct = default)
    {
        var pendingStatus = (int)OutboxStatus.Pending;
        var summaries = await _dbContext.OpsOutboxJobs
            .AsNoTracking()
            .Where(j => j.Status == pendingStatus)
            .OrderBy(j => j.NextAttemptAtUtc)
            .Take(limit)
            .Select(j => new
            {
                j.Id,
                j.ChannelId,
                j.OwnerIdentityId,
                j.RecipientId,
                j.DeliveryRouteType,
                j.Status,
                j.RetryCount,
                j.MaxAttempts,
                j.CreatedAtUtc,
                j.NextAttemptAtUtc
            })
            .ToListAsync(ct);

        return summaries.Select(s => new OutboxJobSummaryReadModel(
            s.Id,
            new ChannelId(s.ChannelId),
            new PublicIdentityId(s.OwnerIdentityId),
            s.RecipientId.HasValue ? new PublicIdentityId(s.RecipientId.Value) : null,
            (DeliveryRouteType)s.DeliveryRouteType,
            (OutboxStatus)s.Status,
            s.RetryCount,
            s.MaxAttempts,
            s.CreatedAtUtc,
            s.NextAttemptAtUtc)).ToList();
    }

    public async Task<IReadOnlyList<OutboxJobSummaryReadModel>> GetFailedJobsAsync(int limit = 50, CancellationToken ct = default)
    {
        var failedStatus = (int)OutboxStatus.Failed;
        var summaries = await _dbContext.OpsOutboxJobs
            .AsNoTracking()
            .Where(j => j.Status == failedStatus)
            .OrderByDescending(j => j.CreatedAtUtc)
            .Take(limit)
            .Select(j => new
            {
                j.Id,
                j.ChannelId,
                j.OwnerIdentityId,
                j.RecipientId,
                j.DeliveryRouteType,
                j.Status,
                j.RetryCount,
                j.MaxAttempts,
                j.CreatedAtUtc,
                j.NextAttemptAtUtc
            })
            .ToListAsync(ct);

        return summaries.Select(s => new OutboxJobSummaryReadModel(
            s.Id,
            new ChannelId(s.ChannelId),
            new PublicIdentityId(s.OwnerIdentityId),
            s.RecipientId.HasValue ? new PublicIdentityId(s.RecipientId.Value) : null,
            (DeliveryRouteType)s.DeliveryRouteType,
            (OutboxStatus)s.Status,
            s.RetryCount,
            s.MaxAttempts,
            s.CreatedAtUtc,
            s.NextAttemptAtUtc)).ToList();
    }

    public async Task<OutboxJobSummaryReadModel?> GetJobSummaryByIdAsync(Guid jobId, CancellationToken ct = default)
    {
        var summary = await _dbContext.OpsOutboxJobs
            .AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => new
            {
                j.Id,
                j.ChannelId,
                j.OwnerIdentityId,
                j.RecipientId,
                j.DeliveryRouteType,
                j.Status,
                j.RetryCount,
                j.MaxAttempts,
                j.CreatedAtUtc,
                j.NextAttemptAtUtc
            })
            .FirstOrDefaultAsync(ct);

        if (summary == null)
        {
            return null;
        }

        return new OutboxJobSummaryReadModel(
            summary.Id,
            new ChannelId(summary.ChannelId),
            new PublicIdentityId(summary.OwnerIdentityId),
            summary.RecipientId.HasValue ? new PublicIdentityId(summary.RecipientId.Value) : null,
            (DeliveryRouteType)summary.DeliveryRouteType,
            (OutboxStatus)summary.Status,
            summary.RetryCount,
            summary.MaxAttempts,
            summary.CreatedAtUtc,
            summary.NextAttemptAtUtc);
    }

    public async Task<IReadOnlyList<OutboxJobSummaryReadModel>> GetJobsForOwnerAsync(PublicIdentityId ownerId, CancellationToken ct = default)
    {
        var ownerGuid = ownerId.Value;
        var summaries = await _dbContext.OpsOutboxJobs
            .AsNoTracking()
            .Where(j => j.OwnerIdentityId == ownerGuid)
            .OrderByDescending(j => j.CreatedAtUtc)
            .Select(j => new
            {
                j.Id,
                j.ChannelId,
                j.OwnerIdentityId,
                j.RecipientId,
                j.DeliveryRouteType,
                j.Status,
                j.RetryCount,
                j.MaxAttempts,
                j.CreatedAtUtc,
                j.NextAttemptAtUtc
            })
            .ToListAsync(ct);

        return summaries.Select(s => new OutboxJobSummaryReadModel(
            s.Id,
            new ChannelId(s.ChannelId),
            new PublicIdentityId(s.OwnerIdentityId),
            s.RecipientId.HasValue ? new PublicIdentityId(s.RecipientId.Value) : null,
            (DeliveryRouteType)s.DeliveryRouteType,
            (OutboxStatus)s.Status,
            s.RetryCount,
            s.MaxAttempts,
            s.CreatedAtUtc,
            s.NextAttemptAtUtc)).ToList();
    }
}
