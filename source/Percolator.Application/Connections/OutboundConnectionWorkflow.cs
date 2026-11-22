namespace Percolator.Application.Connections;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Percolator.Application.Invitations;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;
using Percolator.PluginSdk.Profiles;

public sealed class OutboundConnectionWorkflow : IOutboundConnectionWorkflow
{
    private readonly IPeerConnectionService _connectionService;
    private readonly IPeerMailboxRoutingRepository _routingRepo;
    private readonly IDateTimeProvider _timeProvider;
    private readonly ILogger<OutboundConnectionWorkflow> _logger;

    public OutboundConnectionWorkflow(
        IPeerConnectionService connectionService,
        IPeerMailboxRoutingRepository routingRepo,
        IDateTimeProvider timeProvider,
        ILogger<OutboundConnectionWorkflow> logger)
    {
        _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
        _routingRepo = routingRepo ?? throw new ArgumentNullException(nameof(routingRepo));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DomainResult<OutboundConnectionResult>> ConnectAsync(
        PeerInvitationPayload payload,
        TimeSpan probeTimeout,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (probeTimeout > TimeSpan.Zero)
        {
            cts.CancelAfter(probeTimeout);
        }

        if (payload.RelayEndpoint != null)
        {
            var capability = new PeerMailboxRoutingCapability(
                payload.IdentityId,
                payload.RoutingToken,
                payload.DeliveryToken,
                payload.RelayEndpoint,
                _timeProvider.UtcNow);

            await _routingRepo.SaveAsync(capability, ct).ConfigureAwait(false);

            try
            {
                var connResult = await _connectionService.ConnectToPeerAsync(
                    payload.IdentityId,
                    DeviceId.Primary,
                    payload.RelayEndpoint,
                    ct: cts.Token).ConfigureAwait(false);

                if (connResult.IsSuccess)
                {
                    _logger.LogInformation("Relayed connection to peer {PeerId} succeeded via {RelayEndpoint}", payload.IdentityId, payload.RelayEndpoint);
                    return DomainResult<OutboundConnectionResult>.Success(new OutboundConnectionResult(
                        OutboundConnectionStatus.Connected,
                        payload.IdentityId,
                        payload,
                        null));
                }

                if (IsTransientNetworkError(connResult.Error))
                {
                    _logger.LogInformation("Relayed peer {PeerId} connection returned transient failure ({Error}). Staging as PendingOffline.",
                        payload.IdentityId, connResult.Error.Description);
                    return DomainResult<OutboundConnectionResult>.Success(new OutboundConnectionResult(
                        OutboundConnectionStatus.PendingOffline,
                        payload.IdentityId,
                        payload,
                        null));
                }

                _logger.LogWarning("Relayed peer {PeerId} connection returned unrecoverable error ({Code}: {Error})",
                    payload.IdentityId, connResult.Error.Code, connResult.Error.Description);
                return DomainResult<OutboundConnectionResult>.Failure(connResult.Error);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogInformation("Relayed peer {PeerId} probe timed out after {Timeout}. Staging as PendingOffline.",
                    payload.IdentityId, probeTimeout);
                return DomainResult<OutboundConnectionResult>.Success(new OutboundConnectionResult(
                    OutboundConnectionStatus.PendingOffline,
                    payload.IdentityId,
                    payload,
                    null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Exception encountered during relayed peer probe for {PeerId}. Staging as PendingOffline.", payload.IdentityId);
                return DomainResult<OutboundConnectionResult>.Success(new OutboundConnectionResult(
                    OutboundConnectionStatus.PendingOffline,
                    payload.IdentityId,
                    payload,
                    null));
            }
        }

        Uri directUri;
        try
        {
            var rawHost = payload.EndpointHosts.FirstOrDefault(h => !string.IsNullOrWhiteSpace(h)) ?? "127.0.0.1";
            var port = payload.PortRange?.MinPort ?? 5100;

            if (rawHost.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                rawHost.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                var parsed = new Uri(rawHost);
                directUri = new UriBuilder(parsed) { Scheme = "https", Port = port }.Uri;
            }
            else
            {
                directUri = new UriBuilder("https", rawHost, port).Uri;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse endpoint URI for peer invitation {PeerId}", payload.IdentityId);
            return DomainResult<OutboundConnectionResult>.Failure(new DomainError(
                "INVALID_ENDPOINT", $"Invalid endpoint address in invitation: {ex.Message}"));
        }

        try
        {
            var connResult = await _connectionService.ConnectToDirectPeerAsync(
                payload.IdentityId,
                payload.RoutingToken,
                payload.DeliveryToken,
                directUri,
                DeviceId.Primary,
                ct: cts.Token).ConfigureAwait(false);

            if (connResult.IsSuccess)
            {
                _logger.LogInformation("Direct connection to peer {PeerId} succeeded at {Endpoint}", payload.IdentityId, directUri);
                return DomainResult<OutboundConnectionResult>.Success(new OutboundConnectionResult(
                    OutboundConnectionStatus.Connected,
                    payload.IdentityId,
                    payload,
                    directUri));
            }

            if (IsTransientNetworkError(connResult.Error))
            {
                _logger.LogInformation("Direct peer {PeerId} probe returned transient failure ({Error}). Staging as PendingOffline.",
                    payload.IdentityId, connResult.Error.Description);
                return DomainResult<OutboundConnectionResult>.Success(new OutboundConnectionResult(
                    OutboundConnectionStatus.PendingOffline,
                    payload.IdentityId,
                    payload,
                    directUri));
            }

            _logger.LogWarning("Direct peer {PeerId} connection returned unrecoverable error ({Code}: {Error})",
                payload.IdentityId, connResult.Error.Code, connResult.Error.Description);
            return DomainResult<OutboundConnectionResult>.Failure(connResult.Error);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogInformation("Direct peer {PeerId} probe timed out after {Timeout}. Staging as PendingOffline.",
                payload.IdentityId, probeTimeout);
            return DomainResult<OutboundConnectionResult>.Success(new OutboundConnectionResult(
                OutboundConnectionStatus.PendingOffline,
                payload.IdentityId,
                payload,
                directUri));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Exception encountered during direct peer probe for {PeerId}. Staging as PendingOffline.", payload.IdentityId);
            return DomainResult<OutboundConnectionResult>.Success(new OutboundConnectionResult(
                OutboundConnectionStatus.PendingOffline,
                payload.IdentityId,
                payload,
                directUri));
        }
    }

    private static bool IsTransientNetworkError(DomainError error)
    {
        return error.Code switch
        {
            "PREKEY_BUNDLE_RPC_ERROR" => true,
            "PREKEY_BUNDLE_FETCH_FAILED" => true,
            "PREKEY_BUNDLE_NOT_FOUND" => true,
            "CONNECTION_REFUSED" => true,
            "ENDPOINT_UNREACHABLE" => true,
            "TIMEOUT" => true,
            _ => false
        };
    }
}
