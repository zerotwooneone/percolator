using Percolator.PluginSdk.Events;
using Percolator.Domain.Identities;

namespace Percolator.Application.Session.Events;

/// <summary>
/// Immutable fact that a persona identity left the session's active set —
/// published after the registry mutates, never for no-op removals.
/// </summary>
public sealed record PersonaDeactivatedEvent(PublicIdentityId IdentityId) : IApplicationEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
    public DateTimeOffset OccurredOnUtc { get; } = DateTimeOffset.UtcNow;
}
