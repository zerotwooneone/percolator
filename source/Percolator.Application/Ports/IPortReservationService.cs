using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Percolator.Application.Ports;

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
/// Probes and reserves loopback TCP ports in the canonical Percolator port range (5100–5199 per DEC-CANONICAL-PORT-RANGE).
/// Used by both Desktop and Simulator hosts for consistent port allocation without static state or port collisions.
/// </summary>
public interface IPortReservationService
{
    /// <summary>First port in the reservation range.</summary>
    int RangeStart { get; }

    /// <summary>Last port in the reservation range.</summary>
    int RangeEnd { get; }

    /// <summary>
    /// Transiently checks <paramref name="preferredPort"/>; if taken, scans the range for the
    /// next free port. No socket remains bound after the call returns.
    /// </summary>
    Task<PortProbeResult> ProbeAsync(int preferredPort, CancellationToken ct = default);

    /// <summary>
    /// Checks <paramref name="preferredPort"/> and finds the next free loopback port in the range,
    /// skipping any ports in <paramref name="claimedPorts"/> (e.g. configured in the vault) as well
    /// as any ports currently bound on the host OS.
    /// </summary>
    Task<PortProbeResult> ProbeNextAvailableAsync(
        int preferredPort,
        IReadOnlyCollection<int>? claimedPorts = null,
        CancellationToken ct = default);

    /// <summary>
    /// Reserves a port, holding the socket bound until the lease is disposed. Tries
    /// <paramref name="preferredPort"/> first, then scans upward through the range.
    /// Returns <c>null</c> when the entire range is exhausted.
    /// </summary>
    Task<IPortLease?> AcquireAsync(int preferredPort, CancellationToken ct = default);

    /// <summary>
    /// Synchronously allocates the next available loopback port in the range, recording it
    /// in the active allocation table. Throws <see cref="InvalidOperationException"/> if range exhausted.
    /// </summary>
    int AllocateAvailablePort(int? preferredPort = null);

    /// <summary>Releases a previously allocated port back to the free pool.</summary>
    void ReleasePort(int port);

    /// <summary>Checks whether a port is currently marked as allocated.</summary>
    bool IsPortAllocated(int port);
}

/// <summary>
/// Extended port reservation contract for simulated environments and test harnesses.
/// Segregates test lifecycle reset capabilities from the production port reservation contract.
/// </summary>
public interface ISimulatedPortReservationService : IPortReservationService
{
    /// <summary>Clears all in-memory allocated port records (used during test / scenario resets).</summary>
    void Reset();
}
