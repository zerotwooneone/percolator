namespace Percolator.Network.Messaging;

public sealed class DefaultNetworkSender : INetworkSender
{
    private readonly IRoutePlanner _planner;
    private readonly ISendExecutor _executor;

    public DefaultNetworkSender(IRoutePlanner planner, ISendExecutor executor)
    {
        _planner = planner;
        _executor = executor;
    }

    public async Task<SendOutcome> SendAsync(PeerId target, NetworkPayload payload, SendStrategy strategy, CancellationToken ct = default)
    {
        var plan = await _planner.PlanAsync(target, strategy, ct).ConfigureAwait(false);
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
