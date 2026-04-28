using System.Diagnostics;
using Percolator.Network;
using Percolator.Network.Messaging;

namespace Percolator.Application.Network.Messaging;

public sealed class DefaultSendExecutor : ISendExecutor
{
    private readonly ITransportPort _transport;

    public DefaultSendExecutor(ITransportPort transport)
    {
        _transport = transport;
    }

    public async Task<SendOutcome> ExecuteAsync(PeerId target, NetworkPayload payload, IReadOnlyList<PlannedRoute> plannedRoutes, CancellationToken ct = default)
    {
        var attemptedPaths = new List<string>(plannedRoutes.Count);
        var attemptDetails = new List<AttemptDetail>(plannedRoutes.Count);

        foreach (var route in plannedRoutes)
        {
            ct.ThrowIfCancellationRequested();
            string routeString = route switch
            {
                PlannedRoute.Direct => "Direct",
                PlannedRoute.Relay r => $"Relay:{r.RelayHostPeerId.Value}",
                _ => "Unknown"
            };
            attemptedPaths.Add(routeString);

            var sw = Stopwatch.StartNew();
            bool ok;
            NetworkPayload? response;
            SendFailureReason? reason;
            Exception? error;

            if (route is PlannedRoute.Direct)
            {
                (ok, response, reason, error) = await _transport.SendDirectAsync(target, payload, ct).ConfigureAwait(false);
            }
            else if (route is PlannedRoute.Relay relay)
            {
                (ok, response, reason, error) = await _transport.SendViaRelayAsync(relay.RelayHostPeerId, target, payload, ct).ConfigureAwait(false);
            }
            else
            {
                // Unknown route type
                sw.Stop();
                attemptDetails.Add(new AttemptDetail { Route = routeString, Duration = sw.Elapsed, Reason = SendFailureReason.Unknown });
                continue;
            }

            sw.Stop();
            attemptDetails.Add(new AttemptDetail
            {
                Route = routeString,
                Duration = sw.Elapsed,
                Reason = ok ? null : (reason ?? SendFailureReason.Unknown)
            });

            if (ok)
            {
                return new SendOutcome
                {
                    Success = true,
                    Path = routeString,
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
