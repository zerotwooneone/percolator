using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Identities.ValueObjects;
using R3;

namespace Percolator.Desktop.Common.Services;

/// <summary>
/// Gated network barrier ensuring that Percolator.Desktop operates in total network stealth
/// (zero bound or listening ports, probes, or broadcasts) until the user explicitly selects
/// and confirms one or more active cryptographic identities.
/// </summary>
public interface INetworkStealthGate : IDisposable
{
    /// <summary>
    /// True when stealth mode is active (all network listeners are completely shut down or unbound).
    /// </summary>
    bool IsStealthActive { get; }

    /// <summary>
    /// The currently confirmed and active cryptographic identities.
    /// Empty while stealth mode is active.
    /// </summary>
    IReadOnlyList<PublicIdentityId> ActiveIdentities { get; }

    /// <summary>
    /// Reactive stream signaling transitions between stealth and active network modes.
    /// Emits true when network listeners are active, false when in total stealth mode.
    /// </summary>
    Observable<bool> NetworkActiveChanged { get; }

    /// <summary>
    /// Explicitly activates network subsystems and binds listeners for the confirmed identities.
    /// </summary>
    Task ActivateAsync(IReadOnlyList<PublicIdentityId> identities, CancellationToken ct = default);

    /// <summary>
    /// Immediately closes all listening sockets, tears down network transports, and returns the
    /// application to total network stealth.
    /// </summary>
    Task DeactivateAsync(CancellationToken ct = default);
}
