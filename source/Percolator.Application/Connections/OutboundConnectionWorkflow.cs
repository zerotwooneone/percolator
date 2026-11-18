using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Percolator.Application.Invitations;
using Percolator.Domain.Common;
using Percolator.PluginSdk;

namespace Percolator.Application.Connections;

public sealed class OutboundConnectionWorkflow : IOutboundConnectionWorkflow
{
    private readonly IPeerConnectionService _connectionService;
    private readonly ILogger<OutboundConnectionWorkflow> _logger;

    public OutboundConnectionWorkflow(
        IPeerConnectionService connectionService,
        ILogger<OutboundConnectionWorkflow> logger)
    {
        _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DomainResult<OutboundConnectionResult>> ConnectAsync(
        PeerInvitationPayload payload,
        TimeSpan probeTimeout,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

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

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (probeTimeout > TimeSpan.Zero)
        {
            cts.CancelAfter(probeTimeout);
        }

        try
        {
            var connResult = await _connectionService.ConnectToDirectPeerAsync(
                payload.IdentityId,
                payload.RoutingToken,
                payload.DeliveryToken,
                directUri,
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
            else
            {
                _logger.LogInformation("Direct peer {PeerId} probe returned failure ({Error}). Staging as PendingOffline.",
                    payload.IdentityId, connResult.Error.Description);
                return DomainResult<OutboundConnectionResult>.Success(new OutboundConnectionResult(
                    OutboundConnectionStatus.PendingOffline,
                    payload.IdentityId,
                    payload,
                    directUri));
            }
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
}
