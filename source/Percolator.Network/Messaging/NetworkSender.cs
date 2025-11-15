namespace Percolator.Network.Messaging;

public sealed class DefaultNetworkSender : INetworkSender
{
    private readonly Percolator.Network.IProfileRoutePlanner _planner;
    private readonly Percolator.Network.IPeerRoutingProfileRepository _profiles;
    private readonly IRelayTopology _relayTopology;
    private readonly ISendExecutor _executor;

    public DefaultNetworkSender(
        Percolator.Network.IProfileRoutePlanner planner,
        Percolator.Network.IPeerRoutingProfileRepository profiles,
        IRelayTopology relayTopology,
        ISendExecutor executor)
    {
        _planner = planner;
        _profiles = profiles;
        _relayTopology = relayTopology;
        _executor = executor;
    }

    public async Task<SendOutcome> SendAsync(PeerId target, NetworkPayload payload, SendStrategy strategy, CancellationToken ct = default)
    {
        var plan = new List<string>(capacity: 2);

        try
        {
            var profile = await _profiles.GetByIdAsync(target).ConfigureAwait(false);
            if (profile is not null)
            {
                var selection = _planner.SelectRoute(profile);
                if (selection.Relay is null)
                {
                    plan.Add("Direct");
                }
                else
                {
                    plan.Add($"Relay:{selection.Relay.RelayPeerId.Value}");
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
                    var relayToken = $"Relay:{relay.Value}";
                    if (!plan.Contains(relayToken, StringComparer.Ordinal))
                    {
                        plan.Add(relayToken);
                    }
                }
            }
            catch
            {
                // Ignore and proceed with whatever plan we have
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

        return await _executor.ExecuteAsync(target, payload, plan, ct).ConfigureAwait(false);
    }
}
