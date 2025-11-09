using System.Diagnostics;

namespace Percolator.Network.Messaging;

public sealed class DefaultSendExecutor : ISendExecutor
{
    private readonly ITransportPort _transport;

    public DefaultSendExecutor(ITransportPort transport)
    {
        _transport = transport;
    }

    public async Task<SendOutcome> ExecuteAsync(PeerId target, NetworkPayload payload, IReadOnlyList<string> plannedRoutes, CancellationToken ct = default)
    {
        var attemptedPaths = new List<string>(plannedRoutes.Count);
        var attemptDetails = new List<AttemptDetail>(plannedRoutes.Count);

        foreach (var route in plannedRoutes)
        {
            ct.ThrowIfCancellationRequested();
            attemptedPaths.Add(route);

            var sw = Stopwatch.StartNew();
            bool ok;
            NetworkPayload? response;
            SendFailureReason? reason;
            Exception? error;

            if (string.Equals(route, "Direct", StringComparison.Ordinal))
            {
                (ok, response, reason, error) = await _transport.SendDirectAsync(target, payload, ct).ConfigureAwait(false);
            }
            else if (route.StartsWith("Relay:", StringComparison.Ordinal))
            {
                var relayStr = route.Substring("Relay:".Length);
                if (!Guid.TryParse(relayStr, out var relayGuid))
                {
                    // Malformed route; record and continue
                    sw.Stop();
                    attemptDetails.Add(new AttemptDetail { Route = route, Duration = sw.Elapsed, Reason = SendFailureReason.Unknown });
                    continue;
                }
                var relay = new PeerId(relayGuid);
                (ok, response, reason, error) = await _transport.SendViaRelayAsync(relay, target, payload, ct).ConfigureAwait(false);
            }
            else
            {
                // Unknown route token
                sw.Stop();
                attemptDetails.Add(new AttemptDetail { Route = route, Duration = sw.Elapsed, Reason = SendFailureReason.Unknown });
                continue;
            }

            sw.Stop();
            attemptDetails.Add(new AttemptDetail
            {
                Route = route,
                Duration = sw.Elapsed,
                Reason = ok ? null : (reason ?? SendFailureReason.Unknown)
            });

            if (ok)
            {
                return new SendOutcome
                {
                    Success = true,
                    Path = route.StartsWith("Relay:", StringComparison.Ordinal) ? route : "Direct",
                    AttemptedPaths = attemptedPaths,
                    Attempts = attemptedPaths.Count,
                    AttemptsDetail = attemptDetails,
                    ResponsePayload = response
                };
            }
        }

        // All routes failed
        return new SendOutcome
        {
            Success = false,
            Path = attemptedPaths.Count > 0 ? attemptedPaths[attemptedPaths.Count - 1] : "None",
            AttemptedPaths = attemptedPaths,
            Attempts = attemptedPaths.Count,
            Reason = attemptDetails.LastOrDefault()?.Reason ?? SendFailureReason.Unknown,
            AttemptsDetail = attemptDetails
        };
    }
}
