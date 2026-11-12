using System;
using System.Threading;
using System.Threading.Tasks;

namespace Percolator.Desktop.Common.Services;

/// <summary>Outcome of probing a preferred port ([INV-DESKTOP-STEALTH-ACTIVATION-GATE]).</summary>
public enum PortProbeStatus
{
    /// <summary>The preferred port is free.</summary>
    Ready,

    /// <summary>The preferred port is taken; <c>PortProbeResult.Port</c> holds the auto-selected free port.</summary>
    AutoSelected,

    /// <summary>No port in the reservation range is free.</summary>
    Unavailable
}

/// <summary>Result of a transient port probe — no socket is retained.</summary>
public sealed record PortProbeResult(PortProbeStatus Status, int? Port);

/// <summary>
/// An exclusive, held port reservation. The socket stays bound until <see cref="IDisposable.Dispose"/>;
/// the window handoff coordinator owns lease lifetimes once personas launch ([INV-DESKTOP-TWO-STAGE-HANDOFF]).
/// </summary>
public interface IPortLease : IDisposable
{
    int Port { get; }
}

/// <summary>
/// Probes and reserves loopback TCP ports in the persona activation range (5100–5299).
/// Probing is transient — the pre-activation stealth invariant requires zero retained
/// sockets before the launch command executes ([DEP-DESKTOP-PRE-ACTIVATION-STEALTH]).
/// </summary>
public interface IPortReservationService
{
    /// <summary>First port in the probe range.</summary>
    int RangeStart { get; }

    /// <summary>Last port in the probe range.</summary>
    int RangeEnd { get; }

    /// <summary>
    /// Transiently checks <paramref name="preferredPort"/>; if taken, scans the range for the
    /// next free port. No socket remains bound after the call returns.
    /// </summary>
    Task<PortProbeResult> ProbeAsync(int preferredPort, CancellationToken ct = default);

    /// <summary>
    /// Reserves a port, holding the socket bound until the lease is disposed. Tries
    /// <paramref name="preferredPort"/> first, then scans upward through the range.
    /// Returns <c>null</c> when the entire range is exhausted.
    /// </summary>
    Task<IPortLease?> AcquireAsync(int preferredPort, CancellationToken ct = default);
}
