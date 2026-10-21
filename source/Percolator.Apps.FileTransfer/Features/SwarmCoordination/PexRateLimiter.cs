using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

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
            if (wanPolicy == DirectWanPolicy.DisabledByDefault &&
                candidate.Kind is DirectCandidateKind.DirectWanUpnp or DirectCandidateKind.DirectWanStun)
            {
                continue;
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
}
