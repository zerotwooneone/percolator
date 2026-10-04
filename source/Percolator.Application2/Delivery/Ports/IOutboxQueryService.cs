using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2.Delivery.Ports;

public sealed record OutboxJobSummaryReadModel(
    Guid Id,
    ChannelId ChannelId,
    PublicIdentityId OwnerIdentityId,
    PublicIdentityId? RecipientIdentityId,
    DeliveryRouteType RouteType,
    OutboxStatus Status,
    int RetryCount,
    int MaxAttempts,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? NextAttemptAtUtc);

public interface IOutboxQueryService
{
    Task<IReadOnlyList<OutboxJobSummaryReadModel>> GetPendingJobsAsync(int limit = 50, CancellationToken ct = default);
    Task<IReadOnlyList<OutboxJobSummaryReadModel>> GetFailedJobsAsync(int limit = 50, CancellationToken ct = default);
    Task<OutboxJobSummaryReadModel?> GetJobSummaryByIdAsync(Guid jobId, CancellationToken ct = default);
}
