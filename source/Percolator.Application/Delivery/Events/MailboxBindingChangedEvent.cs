using Percolator.PluginSdk.Events;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Application.Delivery.Events;

public enum MailboxBindingChangeKind { Linked, Unlinked }

/// <summary>
/// Immutable fact that an owner's mailbox binding to a relay changed — published by
/// <c>RelayDirectoryService</c> on link/unlink so the drain scheduler reconciles
/// without waiting for its periodic tick.
/// </summary>
public sealed record MailboxBindingChangedEvent(
    PublicIdentityId OwnerIdentityId,
    PublicIdentityId RelayIdentityId,
    MailboxBindingChangeKind Kind) : IApplicationEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
    public DateTimeOffset OccurredOnUtc { get; } = DateTimeOffset.UtcNow;
}
