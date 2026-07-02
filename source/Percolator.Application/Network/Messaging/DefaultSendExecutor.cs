using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Percolator.Network;
using Percolator.Network.Messaging;

namespace Percolator.Application.Network.Messaging;

public sealed class DefaultSendExecutor : ISendExecutor
{
    private readonly IRouteSender _transport;
    private readonly IRouteConfirmationService _confirmationService;
    private readonly ILogger<DefaultSendExecutor> _logger;

    public DefaultSendExecutor(
        IRouteSender transport,
        IRouteConfirmationService confirmationService,
        ILogger<DefaultSendExecutor> logger)
    {
        _transport = transport;
        _confirmationService = confirmationService;
        _logger = logger;
    }

    public async Task<SendOutcome> ExecuteAsync(uint selfIdentityId, PeerId target, NetworkPayload payload, IReadOnlyList<PlannedRoute> plannedRoutes, CancellationToken ct = default)
    {
        var attemptedPaths = new List<string>(plannedRoutes.Count);
        var attemptDetails = new List<AttemptDetail>(plannedRoutes.Count);
        var nowUtc = DateTimeOffset.UtcNow;

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
            TransportSendResult result;

            if (route is PlannedRoute.Direct)
            {
                result = await _transport.SendDirectAsync(target, payload, ct).ConfigureAwait(false);
            }
            else if (route is PlannedRoute.Relay relay)
            {
                result = await _transport.SendViaRelayAsync(relay.RelayHostPeerId, target, payload, ct).ConfigureAwait(false);
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
                Reason = result.Ok ? null : (result.Reason ?? SendFailureReason.Unknown)
            });

            // Record attempt and promote on success
            await RecordAttemptAndPromoteAsync(
                selfIdentityId,
                target,
                route,
                result,
                nowUtc,
                ct).ConfigureAwait(false);

            if (result.Ok)
            {
                return new SendOutcome
                {
                    Success = true,
                    Path = routeString,
                    AttemptedPaths = attemptedPaths,
                    Attempts = attemptedPaths.Count,
                    AttemptsDetail = attemptDetails,
                    ResponsePayload = result.ResponsePayload
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

    private async Task RecordAttemptAndPromoteAsync(
        uint selfIdentityId,
        PeerId target,
        PlannedRoute route,
        TransportSendResult result,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        try
        {
            var routeKind = route is PlannedRoute.Relay ? RouteKind.Relayed : RouteKind.Direct;
            string? endpointHost = null;
            int? endpointPort = null;
            PeerId? relayHostPeerId = null;

            if (route is PlannedRoute.Relay relay)
            {
                relayHostPeerId = relay.RelayHostPeerId;
            }

            // Convert int to SelfId for Application layer service
            var selfId = new Percolator.Identity.SelfId(selfIdentityId);

            // Record attempt
            await _confirmationService.RecordAttemptAsync(
                selfId,
                target,
                routeKind,
                endpointHost,
                endpointPort,
                relayHostPeerId,
                result.Ok,
                nowUtc,
                ct).ConfigureAwait(false);

            // Promote on success
            if (result.Ok)
            {
                if (routeKind == RouteKind.Direct && result.UsedEndpoint is not null)
                {
                    endpointHost = result.UsedEndpoint.Host;
                    endpointPort = result.UsedEndpoint.Port;
                }

                await _confirmationService.PromoteToConfirmedAsync(
                    selfId,
                    target,
                    routeKind,
                    endpointHost,
                    endpointPort,
                    relayHostPeerId,
                    nowUtc,
                    ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record attempt/promote route for {Target}", target);
        }
    }
}
