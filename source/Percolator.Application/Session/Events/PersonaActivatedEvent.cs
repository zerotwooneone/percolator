using Percolator.PluginSdk.Events;
using Percolator.Domain.Identities;

namespace Percolator.Application.Session.Events;

/// <summary>
/// Immutable fact that a persona identity entered the session's active set —
/// published after the registry mutates, never for no-op additions.
/// </summary>
public sealed record PersonaActivatedEvent(PublicIdentityId IdentityId) : IApplicationEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
    public DateTimeOffset OccurredOnUtc { get; } = DateTimeOffset.UtcNow;
}
