using Grpc.Net.Client;
using Percolator.Contracts;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using IdentityPeerId = Percolator.Identity.PeerId;
using Percolator.Network;
using NetworkPeerId = Percolator.Network.PeerId;
using Percolator.Cryptography;
using Google.Protobuf;

namespace Percolator.Application.Network;

public class GrpcMessageTransportService : IMessageTransportService
{
    private readonly ConcurrentDictionary<string, TransportService.TransportServiceClient> _clients = new();
    private readonly ILogger<GrpcMessageTransportService> _logger;
    private readonly IPeerRoutingProfileRepository _profileRepository;
    private readonly IProfileRoutePlanner _routePlanner;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISimulatorOutboundInterceptor? _simulatorOutbound;

    public GrpcMessageTransportService(
        ILogger<GrpcMessageTransportService> logger,
        IHttpClientFactory httpClientFactory,
        IPeerRoutingProfileRepository profileRepository,
        IProfileRoutePlanner routePlanner,
        ISimulatorOutboundInterceptor? simulatorOutbound = null)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _profileRepository = profileRepository;
        _routePlanner = routePlanner;
        _simulatorOutbound = simulatorOutbound;
    }

    public async Task<DeliverOpaqueMessageResponse> SendMessageAsync(
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
                Payload = ByteString.CopyFrom(message.Value)
            };

            // Interceptor check for simulator-reserved endpoints
            if (_simulatorOutbound is not null)
            {
                var interceptResult = await _simulatorOutbound.InterceptDeliverOpaqueMessageAsync(endPoint.EndPoint, request, cancellationToken).ConfigureAwait(false);
                if (interceptResult.Kind == SimulatorOutboundInterceptResultKind.DeliveredToSimulator)
                {
                    return new DeliverOpaqueMessageResponse { Version = 1 };
                }
                if (interceptResult.Kind == SimulatorOutboundInterceptResultKind.Undeliverable)
                {
                    var destination = interceptResult.Endpoint ?? endPoint.EndPoint;
                    throw new InvalidOperationException(
                        $"Send to simulator-reserved endpoint {destination} failed: {interceptResult.FailureReason ?? "No matching simulated peer"}");
                }
                // NotForSimulator: proceed with normal gRPC send
            }

            // Use the endpoint as the client key, not the peer ID
            var clientKey = $"{endPoint.EndPoint.Host}:{endPoint.EndPoint.Port}";
            var client = GetOrCreateClient(clientKey, endPoint);

            // Add diagnostic logging for the payload
            _logger.LogInformation("Sending message payload with hash: {PayloadHash}, length: {PayloadLength}",
                Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(message.Value)),
                message.Value.Length);
                
            _logger.LogInformation("Sending message to {RecipientPeerId} for conversation {ConversationId}",
                recipientPeerId, directSessionId);
            var response = await client.DeliverOpaqueMessageAsync(request, cancellationToken: cancellationToken);
            _logger.LogInformation("Message sent successfully to {RecipientPeerId}. Response version: {Version}",
                recipientPeerId, response.Version);

            // Legacy last-seen update retained only for legacy path; planner path relies on domain repo updates elsewhere
            return response;
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

                // Use the configured HTTP client from DI with certificate validation settings
                var httpClient = _httpClientFactory.CreateClient("percolator-grpc");
                _logger.LogInformation("Using configured HTTP client for gRPC connection with handler type: {HandlerType}", 
                    httpClient.GetType().Name);

                var channelOptions = new GrpcChannelOptions
                {
                    HttpClient = httpClient,
                    MaxReceiveMessageSize = 4 * 1024 * 1024, // 4 MB
                    MaxSendMessageSize = 4 * 1024 * 1024 // 4 MB
                };

                var uri = new Uri($"http://{endPoint.EndPoint.Host}:{endPoint.EndPoint.Port}");
                _logger.LogInformation("Creating gRPC channel to {Uri} using http", uri);

                var channel = GrpcChannel.ForAddress(uri, channelOptions);
                
                // Test the connection by making a simple ping call
                try
                {
                    var client = new TransportService.TransportServiceClient(channel);
                    _logger.LogInformation("Successfully created gRPC client for {Endpoint}", endPoint);
                    return client;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to create gRPC client for {Endpoint} - channel creation succeeded but client creation failed", endPoint);
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create gRPC client for {Endpoint}", endPoint);
                throw;
            }
        });
    }

}