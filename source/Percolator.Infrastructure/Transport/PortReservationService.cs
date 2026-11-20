using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Application.Ports;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Infrastructure.Transport;

/// <summary>
/// Default <see cref="IPortReservationService"/> probing and reserving loopback TCP ports in the
/// canonical Percolator range (5100–5199 per DEC-CANONICAL-PORT-RANGE).
/// Thread-safe for multi-node simulation while supporting pre-activation stealth transient probing
/// and exclusive port leasing for desktop activation.
/// </summary>
public sealed class PortReservationService : ISimulatedPortReservationService
{
    private readonly object _gate = new();
    private readonly HashSet<int> _allocatedPorts = new();

    public int RangeStart { get; }
    public int RangeEnd { get; }

    public PortReservationService() : this(
        PortRange.DefaultDynamicRange.MinPort,
        PortRange.DefaultDynamicRange.MaxPort)
    {
    }

    /// <summary>Range constructor kept injectable so tests can shrink the scan space.</summary>
    public PortReservationService(int rangeStart, int rangeEnd)
    {
        if (rangeStart <= 0 || rangeEnd > 65535 || rangeStart > rangeEnd)
        {
            throw new ArgumentOutOfRangeException(nameof(rangeStart), "Port range must be within 1–65535.");
        }

        RangeStart = rangeStart;
        RangeEnd = rangeEnd;
    }

    public Task<PortProbeResult> ProbeAsync(int preferredPort, CancellationToken ct = default) =>
        ProbeNextAvailableAsync(preferredPort, null, ct);

    public Task<PortProbeResult> ProbeNextAvailableAsync(
        int preferredPort,
        IReadOnlyCollection<int>? claimedPorts = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var claimedSet = claimedPorts as IReadOnlySet<int>
            ?? (claimedPorts is not null ? new HashSet<int>(claimedPorts) : null);

        bool isClaimed;
        lock (_gate)
        {
            isClaimed = _allocatedPorts.Contains(preferredPort) || (claimedSet?.Contains(preferredPort) ?? false);
        }

        if (!isClaimed && preferredPort >= RangeStart && preferredPort <= RangeEnd && IsPortAvailable(preferredPort))
        {
            return Task.FromResult(new PortProbeResult(PortProbeStatus.Ready, preferredPort));
        }

        foreach (var candidate in EnumerateCandidates(preferredPort))
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_allocatedPorts.Contains(candidate) || (claimedSet?.Contains(candidate) == true))
                {
                    continue;
                }
            }

            if (IsPortAvailable(candidate))
            {
                return Task.FromResult(new PortProbeResult(PortProbeStatus.AutoSelected, candidate));
            }
        }

        return Task.FromResult(new PortProbeResult(PortProbeStatus.Unavailable, null));
    }

    public Task<IPortLease?> AcquireAsync(int preferredPort, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_allocatedPorts.Contains(preferredPort) && preferredPort >= RangeStart && preferredPort <= RangeEnd && TryBind(preferredPort, out var preferred))
            {
                _allocatedPorts.Add(preferredPort);
                return Task.FromResult<IPortLease?>(new PortLease(preferredPort, preferred, this));
            }

            foreach (var candidate in EnumerateCandidates(preferredPort))
            {
                ct.ThrowIfCancellationRequested();
                if (_allocatedPorts.Contains(candidate))
                {
                    continue;
                }

                if (TryBind(candidate, out var listener))
                {
                    _allocatedPorts.Add(candidate);
                    return Task.FromResult<IPortLease?>(new PortLease(candidate, listener, this));
                }
            }

            return Task.FromResult<IPortLease?>(null);
        }
    }

    public int AllocateAvailablePort(int? preferredPort = null)
    {
        lock (_gate)
        {
            if (preferredPort.HasValue &&
                preferredPort.Value >= RangeStart &&
                preferredPort.Value <= RangeEnd &&
                !_allocatedPorts.Contains(preferredPort.Value) &&
                IsPortAvailable(preferredPort.Value))
            {
                _allocatedPorts.Add(preferredPort.Value);
                return preferredPort.Value;
            }

            var candidates = preferredPort.HasValue
                ? EnumerateCandidates(preferredPort.Value)
                : EnumerateRange();

            foreach (var port in candidates)
            {
                if (!_allocatedPorts.Contains(port) && IsPortAvailable(port))
                {
                    _allocatedPorts.Add(port);
                    return port;
                }
            }

            throw new InvalidOperationException(
                $"No available loopback TCP ports in canonical Percolator range {RangeStart}–{RangeEnd}.");
        }
    }

    private IEnumerable<int> EnumerateRange()
    {
        for (var port = RangeStart; port <= RangeEnd; port++)
        {
            yield return port;
        }
    }

    public void ReleasePort(int port)
    {
        lock (_gate)
        {
            _allocatedPorts.Remove(port);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _allocatedPorts.Clear();
        }
    }

    public bool IsPortAllocated(int port)
    {
        lock (_gate)
        {
            return _allocatedPorts.Contains(port);
        }
    }

    private IEnumerable<int> EnumerateCandidates(int preferredPort)
    {
        for (var port = Math.Max(preferredPort + 1, RangeStart); port <= RangeEnd; port++)
        {
            yield return port;
        }

        for (var port = RangeStart; port < Math.Min(preferredPort, RangeEnd + 1); port++)
        {
            yield return port;
        }
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            using (var loopback = new TcpListener(IPAddress.Loopback, port))
            {
                loopback.Start();
                loopback.Stop();
            }

            using (var any = new TcpListener(IPAddress.Any, port))
            {
                any.Start();
                any.Stop();
            }

            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static bool TryBind(int port, out TcpListener listener)
    {
        try
        {
            var candidate = new TcpListener(IPAddress.Loopback, port);
            candidate.Start();
            listener = candidate;
            return true;
        }
        catch (SocketException)
        {
            listener = null!;
            return false;
        }
    }

    private sealed class PortLease : IPortLease
    {
        private readonly TcpListener _listener;
        private readonly PortReservationService _service;
        private bool _disposed;

        public PortLease(int port, TcpListener listener, PortReservationService service)
        {
            Port = port;
            _listener = listener;
            _service = service;
        }

        public int Port { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _listener.Stop();
            _service.ReleasePort(Port);
        }
    }
}
