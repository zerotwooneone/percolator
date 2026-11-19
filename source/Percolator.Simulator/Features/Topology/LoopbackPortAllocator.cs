using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Simulator.Features.Topology;

/// <summary>
/// Allocates available TCP ports within the canonical Percolator port range (5100–5199 inclusive)
/// for simulated persona and relay hosts per DEC-CANONICAL-PORT-RANGE.
/// </summary>
internal static class LoopbackPortAllocator
{
    private static readonly object Gate = new();
    private static readonly HashSet<int> AllocatedPorts = new();

    public static int RangeStart => PortRange.DefaultDynamicRange.MinPort; // 5100
    public static int RangeEnd => PortRange.DefaultDynamicRange.MaxPort;     // 5199

    public static int AllocateAvailablePort(int? preferredPort = null)
    {
        lock (Gate)
        {
            if (preferredPort.HasValue &&
                preferredPort.Value >= RangeStart &&
                preferredPort.Value <= RangeEnd &&
                !AllocatedPorts.Contains(preferredPort.Value) &&
                IsPortAvailable(preferredPort.Value))
            {
                AllocatedPorts.Add(preferredPort.Value);
                return preferredPort.Value;
            }

            for (var port = RangeStart; port <= RangeEnd; port++)
            {
                if (!AllocatedPorts.Contains(port) && IsPortAvailable(port))
                {
                    AllocatedPorts.Add(port);
                    return port;
                }
            }

            throw new InvalidOperationException(
                $"No available loopback TCP ports in canonical Percolator range {RangeStart}–{RangeEnd}.");
        }
    }

    public static void ReleasePort(int port)
    {
        lock (Gate)
        {
            AllocatedPorts.Remove(port);
        }
    }

    public static void Reset()
    {
        lock (Gate)
        {
            AllocatedPorts.Clear();
        }
    }

    public static bool IsPortAllocated(int port)
    {
        lock (Gate)
        {
            return AllocatedPorts.Contains(port);
        }
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            using (var loopbackListener = new TcpListener(IPAddress.Loopback, port))
            {
                loopbackListener.Start();
                loopbackListener.Stop();
            }

            using (var anyListener = new TcpListener(IPAddress.Any, port))
            {
                anyListener.Start();
                anyListener.Stop();
            }

            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
