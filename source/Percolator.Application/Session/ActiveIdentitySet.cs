using System.Collections.Immutable;
using Percolator.Application.Ports;
using Percolator.Application.Session.Events;
using Percolator.Domain.Identities;

namespace Percolator.Application.Session;

/// <summary>
/// Lock-guarded <see cref="IActiveIdentitySet"/> that publishes activation facts
/// only on observable state transitions. <see cref="Current"/> is replaced with a
/// fresh immutable snapshot on every mutation so readers enumerate lock-free.
/// </summary>
public sealed class ActiveIdentitySet : IActiveIdentitySet
{
    private readonly IEventPublisher _publisher;
    private readonly object _gate = new();
    private readonly HashSet<PublicIdentityId> _active = new();
    private IReadOnlyCollection<PublicIdentityId> _current = Array.Empty<PublicIdentityId>();

    public ActiveIdentitySet(IEventPublisher publisher)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        _publisher = publisher;
    }

    public IReadOnlyCollection<PublicIdentityId> Current => _current;

    public bool Add(PublicIdentityId identityId)
    {
        lock (_gate)
        {
            if (!_active.Add(identityId))
            {
                return false;
            }

            _current = _active.ToImmutableArray();
        }

        // Publish outside the lock so a slow subscriber cannot block registry mutation.
        // Dispatch is fire-and-forget: the synchronous contract cannot await, and
        // InMemoryEventBus routes subscriber faults to registered fault handlers.
        _ = _publisher.PublishAsync(new PersonaActivatedEvent(identityId));
        return true;
    }

    public bool Remove(PublicIdentityId identityId)
    {
        lock (_gate)
        {
            if (!_active.Remove(identityId))
            {
                return false;
            }

            _current = _active.ToImmutableArray();
        }

        _ = _publisher.PublishAsync(new PersonaDeactivatedEvent(identityId));
        return true;
    }
}
