using System.Net;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Percolator.Contracts;
using Percolator.Network;
using Percolator.Network.Services;

namespace Percolator.Infrastructure.Network.Grpc
{
    /// <summary>
    /// Service for establishing gRPC sessions with remote peers.
    /// Acts as a lightweight bridge between Domain session requests and the gRPC client.
    /// All TLS logic and channel management is handled by IPeerGrpcChannelFactory.
    /// </summary>
    public class GrpcSessionService : ISessionEstablishmentTransport
    {
        private readonly IPeerGrpcChannelFactory _channelFactory;
        private readonly ILogger<GrpcSessionService> _logger;
        private readonly ISimulatorOutboundInterceptor? _simulatorOutbound;

        public GrpcSessionService(
            IPeerGrpcChannelFactory channelFactory,
            ILogger<GrpcSessionService> logger,
            ISimulatorOutboundInterceptor? simulatorOutbound = null)
        {
            _channelFactory = channelFactory;
            _logger = logger;
            _simulatorOutbound = simulatorOutbound;
        }
        
        public async Task<EstablishDirectSessionResponse> EstablishDirectSessionAsync(
            DnsEndPoint endpoint,
            EstablishDirectSessionRequest request,
            CancellationToken cancellationToken = default)
        {
            if (_simulatorOutbound is not null &&
                _simulatorOutbound.TryEstablishDirectSession(endpoint, request, cancellationToken, out var simulated))
            {
                return await simulated.ConfigureAwait(false);
            }

            try
            {
                _logger.LogInformation("Establishing direct session with {Endpoint}", endpoint);

                var channel = _channelFactory.CreateChannel(endpoint);
                var client = new TransportService.TransportServiceClient(channel);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(300));
                return await client.EstablishDirectSessionAsync(request, cancellationToken: cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not RpcException)
            {
                _logger.LogError(ex, "Failed to establish direct gRPC session with {Endpoint}: {ErrorMessage}", endpoint, ex.Message);
                throw new RpcException(new Status(StatusCode.Unavailable, ex.Message));
            }
        }

        public async Task<EstablishSessionResponse> EstablishSessionAsync(
            DnsEndPoint endpoint,
            EstablishSessionRequest request,
            CancellationToken cancellationToken = default)
        {
            if (_simulatorOutbound is not null &&
                _simulatorOutbound.TryEstablishSession(endpoint, request, cancellationToken, out var simulated))
            {
                return await simulated.ConfigureAwait(false);
            }

            try
            {
                _logger.LogInformation("Sending EstablishSession request to {Endpoint}", endpoint);

                // Get the physical pipe from the centralized factory
                var channel = _channelFactory.CreateChannel(endpoint);

                // Instantiate the gRPC client
                var client = new TransportService.TransportServiceClient(channel);

                // Make the call (Timeout logic remains here)
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(300));

                return await client.EstablishSessionAsync(request, cancellationToken: cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not RpcException)
            {
                _logger.LogError(ex, "Failed to send EstablishSession to {Endpoint}: {ErrorMessage}", endpoint, ex.Message);
                throw new RpcException(new Status(StatusCode.Unavailable, ex.Message));
            }
        }

        public async Task<DeliverInviteHandshakeResponseAck> DeliverInviteHandshakeResponseAsync(
            DnsEndPoint endpoint,
            InviteHandshakeResponse request,
            CancellationToken cancellationToken = default)
        {
            if (_simulatorOutbound is not null &&
                _simulatorOutbound.TryDeliverInviteHandshakeResponse(endpoint, request, cancellationToken, out var simulated))
            {
                return await simulated.ConfigureAwait(false);
            }

            try
            {
                _logger.LogInformation("Delivering InviteHandshakeResponse to {Endpoint}", endpoint);

                var channel = _channelFactory.CreateChannel(endpoint);
                var client = new TransportService.TransportServiceClient(channel);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(300));
                return await client.DeliverInviteHandshakeResponseAsync(request, cancellationToken: cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not RpcException)
            {
                _logger.LogError(ex, "Failed to deliver InviteHandshakeResponse to {Endpoint}: {ErrorMessage}", endpoint, ex.Message);
                throw new RpcException(new Status(StatusCode.Unavailable, ex.Message));
            }
        }

    }
}
