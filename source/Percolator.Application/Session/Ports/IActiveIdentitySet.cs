using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Identities;

namespace Percolator.Application.Session.Ports;

/// <summary>
/// Session-scoped registry of persona identities activated in the main shell.
/// Background delivery workers reconcile their drain targets against
/// <see cref="Current"/> and activation events.
/// </summary>
public interface IActiveIdentitySet
{
    /// <summary>Immutable snapshot of the currently active identities.</summary>
    IReadOnlyCollection<PublicIdentityId> Current { get; }

    /// <summary>Returns true if the given identity is currently active in the session.</summary>
    bool Contains(PublicIdentityId identityId);

    /// <summary>Marks an identity active asynchronously and awaits event publication. Returns false when already active.</summary>
    ValueTask<bool> AddAsync(PublicIdentityId identityId, CancellationToken ct = default);

    /// <summary>Marks an identity inactive asynchronously and awaits event publication. Returns false when not active.</summary>
    ValueTask<bool> RemoveAsync(PublicIdentityId identityId, CancellationToken ct = default);

    /// <summary>Marks an identity active. Returns false when already active.</summary>
    bool Add(PublicIdentityId identityId);

    /// <summary>Marks an identity inactive. Returns false when not active.</summary>
    bool Remove(PublicIdentityId identityId);
}
