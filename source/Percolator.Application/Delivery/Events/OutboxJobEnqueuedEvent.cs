using System;
using Percolator.Domain.Identities;
using Percolator.PluginSdk.Events;

namespace Percolator.Application.Delivery.Events;

public sealed record OutboxJobEnqueuedEvent(
    Guid JobId,
    PublicIdentityId OwnerIdentityId,
    DateTimeOffset OccurredOnUtc) : IApplicationEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}
