using System.Net;
using System.Net.Sockets;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Apps.FileTransfer.Features.SwarmCoordination;

public sealed class PexRateLimiter
{
    public static readonly TimeSpan MinPexInterval = TimeSpan.FromSeconds(10);
    public const int MaxCandidatesPerFrame = 16;

    private readonly Dictionary<PublicIdentityId, DateTimeOffset> _lastPexReceivedByPeer = [];

    public bool CanReceivePex(PublicIdentityId peerId, IDateTimeProvider clock)
    {
        DateTimeOffset now = clock.UtcNow;
        if (_lastPexReceivedByPeer.TryGetValue(peerId, out DateTimeOffset lastTime))
        {
            if (now - lastTime < MinPexInterval)
                return false;
        }

        _lastPexReceivedByPeer[peerId] = now;
        return true;
    }

    public static IReadOnlyList<DirectEndpointCandidate> FilterAndDeduplicate(
        IReadOnlyList<DirectEndpointCandidate> candidates,
        DirectWanPolicy wanPolicy,
        IReadOnlyCollection<DirectEndpointCandidate>? localEndpoints = null)
    {
        if (candidates == null || candidates.Count == 0)
            return [];

        HashSet<string> seen = [];
        HashSet<string> localSet = [];

        if (localEndpoints != null)
        {
            foreach (var local in localEndpoints)
            {
                localSet.Add($"{local.Host.Value}:{local.Port.Value}");
            }
        }

        List<DirectEndpointCandidate> filtered = [];

        foreach (DirectEndpointCandidate candidate in candidates)
        {
            if (filtered.Count >= MaxCandidatesPerFrame)
                break;

            // Enforce WAN Policy Gate: In DisabledByDefault mode, public WAN candidates are dropped
            if (wanPolicy == DirectWanPolicy.DisabledByDefault)
            {
                if (candidate.Kind is DirectCandidateKind.DirectWanUpnp or DirectCandidateKind.DirectWanStun)
                {
                    continue;
                }

                if (IPAddress.TryParse(candidate.Host.Value, out IPAddress? ip) && !IsPrivateOrLocal(ip))
                {
                    continue;
                }
            }

            string key = $"{candidate.Host.Value}:{candidate.Port.Value}";
            if (localSet.Contains(key) || !seen.Add(key))
            {
                continue; // Deduplicate and filter local endpoint
            }

            filtered.Add(candidate);
        }

        return filtered;
    }

    private static bool IsPrivateOrLocal(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            if (bytes[0] == 10) return true;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            if (bytes[0] == 100 && (bytes[1] & 0xC0) == 64) return true;
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal) return true;
            if ((bytes[0] & 0xFE) == 0xFC) return true;
            return false;
        }

        return false;
    }
}
