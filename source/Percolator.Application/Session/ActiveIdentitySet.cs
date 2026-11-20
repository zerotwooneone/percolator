using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Percolator.Application.Ports;
using Percolator.Application.Session.Events;
using Percolator.Application.Session.Ports;
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
    private readonly ILogger<ActiveIdentitySet> _logger;
    private readonly object _gate = new();
    private readonly HashSet<PublicIdentityId> _active = new();
    private IReadOnlyCollection<PublicIdentityId> _current = Array.Empty<PublicIdentityId>();

    public ActiveIdentitySet(IEventPublisher publisher, ILogger<ActiveIdentitySet> logger)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IReadOnlyCollection<PublicIdentityId> Current => _current;

    public bool Contains(PublicIdentityId identityId)
    {
        lock (_gate)
        {
            return _active.Contains(identityId);
        }
    }

    public async ValueTask<bool> AddAsync(PublicIdentityId identityId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_active.Add(identityId))
            {
                return false;
            }

            _current = _active.ToImmutableArray();
        }

        // Await publish outside the lock so subscriber operations do not hold the mutation gate
        await _publisher.PublishAsync(new PersonaActivatedEvent(identityId), ct).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<bool> RemoveAsync(PublicIdentityId identityId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_active.Remove(identityId))
            {
                return false;
            }

            _current = _active.ToImmutableArray();
        }

        await _publisher.PublishAsync(new PersonaDeactivatedEvent(identityId), ct).ConfigureAwait(false);
        return true;
    }

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

        // Non-blocking fire-and-forget for synchronous caller compatibility (e.g. reactive streams).
        // Dispatches via Task with explicit error logging rather than unobserved ValueTask discard.
        _ = SafePublishAsync(new PersonaActivatedEvent(identityId));
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

        _ = SafePublishAsync(new PersonaDeactivatedEvent(identityId));
        return true;
    }

    private async Task SafePublishAsync<TEvent>(TEvent @event) where TEvent : IApplicationEvent
    {
        try
        {
            await _publisher.PublishAsync(@event).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background dispatch failed for event {EventType}", typeof(TEvent).Name);
        }
    }
}
