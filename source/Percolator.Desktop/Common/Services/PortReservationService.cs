using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Percolator.Desktop.Common.Services;

/// <summary>
/// Default <see cref="IPortReservationService"/> probing loopback TCP ports in the 5100–5299
/// persona activation range. Probes open and immediately close; acquired leases hold the
/// bound listener so a later bind cannot be claimed by another process in the meantime.
/// </summary>
public sealed class PortReservationService : IPortReservationService
{
    private const int DefaultRangeStart = 5100;
    private const int DefaultRangeEnd = 5299;

    public int RangeStart { get; }
    public int RangeEnd { get; }

    public PortReservationService() : this(DefaultRangeStart, DefaultRangeEnd)
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

    public Task<PortProbeResult> ProbeAsync(int preferredPort, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (TryBind(preferredPort, out var preferred))
        {
            preferred.Stop();
            return Task.FromResult(new PortProbeResult(PortProbeStatus.Ready, preferredPort));
        }

        foreach (var candidate in EnumerateCandidates(preferredPort))
        {
            ct.ThrowIfCancellationRequested();
            if (TryBind(candidate, out var listener))
            {
                listener.Stop();
                return Task.FromResult(new PortProbeResult(PortProbeStatus.AutoSelected, candidate));
            }
        }

        return Task.FromResult(new PortProbeResult(PortProbeStatus.Unavailable, null));
    }

    public Task<IPortLease?> AcquireAsync(int preferredPort, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (TryBind(preferredPort, out var preferred))
        {
            return Task.FromResult<IPortLease?>(new PortLease(preferredPort, preferred));
        }

        foreach (var candidate in EnumerateCandidates(preferredPort))
        {
            ct.ThrowIfCancellationRequested();
            if (TryBind(candidate, out var listener))
            {
                return Task.FromResult<IPortLease?>(new PortLease(candidate, listener));
            }
        }

        return Task.FromResult<IPortLease?>(null);
    }

    private System.Collections.Generic.IEnumerable<int> EnumerateCandidates(int preferredPort)
    {
        // Scan upward from the preferred port inside the range, then wrap to the range start
        // so personas are not biased toward only higher ports.
        for (var port = Math.Max(preferredPort + 1, RangeStart); port <= RangeEnd; port++)
        {
            yield return port;
        }

        for (var port = RangeStart; port < Math.Min(preferredPort, RangeStart); port++)
        {
            yield return port;
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
        private bool _disposed;

        public PortLease(int port, TcpListener listener)
        {
            Port = port;
            _listener = listener;
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
        }
    }
}
