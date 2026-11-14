using Percolator.Domain.Identities;

namespace Percolator.Application.Session;

/// <summary>
/// Session-scoped registry of persona identities activated in the main shell.
/// Background delivery workers reconcile their drain targets against
/// <see cref="Current"/> and activation events.
/// </summary>
public interface IActiveIdentitySet
{
    /// <summary>Immutable snapshot of the currently active identities.</summary>
    IReadOnlyCollection<PublicIdentityId> Current { get; }

    /// <summary>Marks an identity active. Returns false when already active.</summary>
    bool Add(PublicIdentityId identityId);

    /// <summary>Marks an identity inactive. Returns false when not active.</summary>
    bool Remove(PublicIdentityId identityId);
}
