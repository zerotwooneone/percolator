using Percolator.Contracts;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using IdentityPeerId = Percolator.Identity.PeerId;
using Percolator.Network;
using Percolator.Cryptography;
using Google.Protobuf;
using Percolator.Application.Network;
using System.Net;

namespace Percolator.Infrastructure.Network.Grpc;

public class GrpcMessageTransportService : IMessageTransportService
{
    private readonly ConcurrentDictionary<string, TransportService.TransportServiceClient> _clients = new();
    private readonly ILogger<GrpcMessageTransportService> _logger;
    private readonly IPeerRoutingProfileRepository _profileRepository;
    private readonly IProfileRoutePlanner _routePlanner;
    private readonly IPeerGrpcChannelFactory _channelFactory;
    private readonly ISimulatorOutboundInterceptor? _simulatorOutbound;

    public GrpcMessageTransportService(
        ILogger<GrpcMessageTransportService> logger,
        IPeerGrpcChannelFactory channelFactory,
        IPeerRoutingProfileRepository profileRepository,
        IProfileRoutePlanner routePlanner,
        ISimulatorOutboundInterceptor? simulatorOutbound = null)
    {
        _logger = logger;
        _channelFactory = channelFactory;
        _profileRepository = profileRepository;
        _routePlanner = routePlanner;
        _simulatorOutbound = simulatorOutbound;
    }

    public async Task<SendMessageResponse> SendMessageAsync(
        IdentityPeerId recipientPeerId, 
        DirectSessionId directSessionId,
        SessionRatchetMessage message,
        CancellationToken cancellationToken = default)
    {
        // Use the recipient identity's GUID directly to address the network peer
        var networkPeerId = new NetworkPeerId(recipientPeerId.Value);
        // Prefer domain routing profile + planner
        GrpcEndPoint? endPoint = null;
        var profile = await _profileRepository.GetByIdAsync(networkPeerId, cancellationToken).ConfigureAwait(false);
        if (profile is not null)
        {
            var selection = _routePlanner.SelectRoute(profile);
            if (selection.Relay is null)
            {
                endPoint = selection.Endpoint;
                _logger.LogInformation("Planner selected endpoint {Endpoint} for {Peer}", endPoint, recipientPeerId);
            }
        }
        if (endPoint is null)
        {
            throw new InvalidOperationException($"No route available for peer {recipientPeerId}. Cannot send message.");
        }

        try
        {
            var request = new DeliverOpaqueMessageRequest
            {
                Version = 1,
                Payload = ByteString.CopyFrom(message.ToArray())
            };

            // Interceptor check for simulator-reserved endpoints
            if (_simulatorOutbound is not null)
            {
                var interceptResult = await _simulatorOutbound.InterceptDeliverOpaqueMessageAsync(endPoint.EndPoint, request, cancellationToken).ConfigureAwait(false);
                switch (interceptResult)
                {
                    case SimulatorOutboundInterceptResult.DeliveredToSimulator delivered:
                        return new SendMessageResponse { OriginalResponse = delivered.Response, UsedEndpoint = endPoint };
                    case SimulatorOutboundInterceptResult.Undeliverable undeliverable:
                        throw new InvalidOperationException(
                            $"Send to simulator-reserved endpoint {undeliverable.Endpoint} failed: {undeliverable.FailureReason}");
                    case SimulatorOutboundInterceptResult.NotForSimulator:
                        // Proceed with normal gRPC send
                        break;
                }
            }

            // Use the endpoint as the client key, not the peer ID
            var clientKey = $"{endPoint.EndPoint.Host}:{endPoint.EndPoint.Port}";
            var client = GetOrCreateClient(clientKey, endPoint);

            // Add diagnostic logging for the payload
            _logger.LogInformation("Sending message payload with hash: {PayloadHash}, length: {PayloadLength}",
                Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(message.ToArray())),
                message.ToArray().Length);
                
            _logger.LogInformation("Sending message to {RecipientPeerId} for conversation {ConversationId}",
                recipientPeerId, directSessionId);
            var response = await client.DeliverOpaqueMessageAsync(request, cancellationToken: cancellationToken);
            _logger.LogInformation("Message sent successfully to {RecipientPeerId}. Response version: {Version}",
                recipientPeerId, response.Version);

            // Legacy last-seen update retained only for legacy path; planner path relies on domain repo updates elsewhere
            return new SendMessageResponse { OriginalResponse = response, UsedEndpoint = endPoint };
        }
        catch (InvalidProtocolBufferException ex)
        {
            _logger.LogError(ex, "Failed to serialize message for transport to {RecipientPeerId}", recipientPeerId);
            throw new Exception("Failed to serialize message for transport", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send message to {RecipientPeerId} for conversation {ConversationId}",
                recipientPeerId, directSessionId);
            throw;
        }
    }

    private TransportService.TransportServiceClient GetOrCreateClient(string clientKey, GrpcEndPoint endPoint)
    {
        return _clients.GetOrAdd(clientKey, _ =>
        {
            try
            {
                _logger.LogInformation("Creating new gRPC client for {Endpoint}", endPoint);

                // Use the centralized channel factory which handles TLS and connection pooling
                var channel = _channelFactory.CreateChannel(new DnsEndPoint(endPoint.EndPoint.Host, endPoint.EndPoint.Port));
                
                _logger.LogInformation("Successfully created gRPC channel for {Endpoint}", endPoint);
                return new TransportService.TransportServiceClient(channel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create gRPC client for {Endpoint}", endPoint);
                throw;
            }
        });
    }

}
