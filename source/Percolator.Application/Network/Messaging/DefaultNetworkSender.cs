using Percolator.Network;
using Percolator.Network.Messaging;

namespace Percolator.Application.Network.Messaging;

public sealed class DefaultNetworkSender : INetworkSender
{
    private readonly Percolator.Network.IProfileRoutePlanner _planner;
    private readonly Percolator.Network.IPeerRoutingProfileRepository _profiles;
    private readonly IRelayTopology _relayTopology;
    private readonly ISendExecutor _executor;
    private readonly IPeerRouteCandidateRepository _candidateRepository;

    public DefaultNetworkSender(
        Percolator.Network.IProfileRoutePlanner planner,
        Percolator.Network.IPeerRoutingProfileRepository profiles,
        IRelayTopology relayTopology,
        ISendExecutor executor,
        IPeerRouteCandidateRepository candidateRepository)
    {
        _planner = planner;
        _profiles = profiles;
        _relayTopology = relayTopology;
        _executor = executor;
        _candidateRepository = candidateRepository;
    }

    public async Task<SendOutcome> SendAsync(int selfIdentityId, PeerId target, NetworkPayload payload, SendStrategy strategy, CancellationToken ct = default)
    {
        var plan = new List<PlannedRoute>(capacity: 2);

        try
        {
            var profile = await _profiles.GetByIdAsync(target).ConfigureAwait(false);
            if (profile is not null)
            {
                var selection = _planner.SelectRoute(profile);
                if (selection.Relay is null)
                {
                    plan.Add(new PlannedRoute.Direct());
                }
                else
                {
                    plan.Add(new PlannedRoute.Relay(selection.Relay.RelayPeerId));
                }
            }
        }
        catch
        {
            // Swallow and let executor surface reasons during execution
        }

        if (strategy == SendStrategy.DirectThenRelay)
        {
            try
            {
                var relay = await _relayTopology.GetRelayForAsync(target, ct).ConfigureAwait(false);
                if (relay is not null)
                {
                    var relayRoute = new PlannedRoute.Relay(relay);
                    // Check if we already have this relay in the plan
                    var hasRelay = plan.OfType<PlannedRoute.Relay>().Any(r => r.RelayHostPeerId.Value == relay.Value);
                    if (!hasRelay)
                    {
                        plan.Add(relayRoute);
                    }
                }
            }
            catch
            {
                // Ignore and proceed with whatever plan we have
            }
        }

        // Phase 1: Fallback to candidate routes if confirmed profile yields no routes
        if (plan.Count == 0)
        {
            try
            {
                var candidates = await _candidateRepository.GetCandidatesAsync(selfIdentityId, target, ct).ConfigureAwait(false);
                // Build best-effort plan from candidates, limit to 3
                foreach (var candidate in candidates.Take(3))
                {
                    if (candidate.RouteKind == RouteKind.Direct)
                    {
                        plan.Add(new PlannedRoute.Direct());
                    }
                    else if (candidate.RouteKind == RouteKind.Relayed && candidate.RelayHostPeerId.HasValue)
                    {
                        plan.Add(new PlannedRoute.Relay(new PeerId(candidate.RelayHostPeerId.Value)));
                    }
                }
            }
            catch
            {
                // Ignore candidate fallback errors and proceed with empty plan
            }
        }

        if (plan.Count == 0)
        {
            return new SendOutcome
            {
                Success = false,
                Path = "None",
                AttemptedPaths = Array.Empty<string>(),
                Attempts = 0,
                Reason = strategy == SendStrategy.DirectOnly ? SendFailureReason.NoEndpoints : SendFailureReason.NoRelayConfigured,
                AttemptsDetail = Array.Empty<AttemptDetail>()
            };
        }

        return await _executor.ExecuteAsync(selfIdentityId, target, payload, plan, ct).ConfigureAwait(false);
    }
}
