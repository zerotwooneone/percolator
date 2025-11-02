using Grpc.Net.Client;
using Percolator.Contracts;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Percolator.Identity;
using IdentityPeerId = Percolator.Identity.PeerId;
using Percolator.Network;
using NetworkPeerId = Percolator.Network.PeerId;
using Percolator.Chat.ValueObjects;
using Percolator.Cryptography;
using Google.Protobuf;

namespace Percolator.Application.Network;

public class GrpcMessageTransportService : IMessageTransportService
{
    private readonly ConcurrentDictionary<string, TransportService.TransportServiceClient> _clients = new();
    private readonly ILogger<GrpcMessageTransportService> _logger;
    private readonly IPeerRepository _peerRepository;
    private readonly IPeerConnectionRepository _peerConnectionRepository;
    private readonly IHttpClientFactory _httpClientFactory;

    public GrpcMessageTransportService(
        ILogger<GrpcMessageTransportService> logger,
        IPeerRepository peerRepository,
        IPeerConnectionRepository peerConnectionRepository,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _peerRepository = peerRepository;
        _peerConnectionRepository = peerConnectionRepository;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<DeliverOpaqueMessageResponse> SendMessageAsync(
        IdentityPeerId recipientPeerId, 
        DirectSessionId directSessionId,
        SessionRatchetMessage message,
        CancellationToken cancellationToken = default)
    {
        var peer = await _peerRepository.GetByIdAsync(recipientPeerId);
        if (peer is null)
        {
            _logger.LogError("Could not find peer with ID {PeerId}", recipientPeerId);
            throw new InvalidOperationException($"Peer not found: {recipientPeerId}");
        }

        var networkPeerId = new NetworkPeerId(peer.Id.Value);
        var peerConnection = await _peerConnectionRepository.GetByIdAsync(networkPeerId);
        if (peerConnection is null)
        {
            throw new InvalidOperationException($"No connection info found for peer {peer.Id}. Cannot send message.");
        }

        if (peerConnection.GrpcEndPoints.Count == 0)
        {
            throw new InvalidOperationException($"No gRPC endpoints found for peer {peer.Id}. Cannot send message.");
        }

        //todo: loop over all the connections and try to send the message to all of them sequentially
        var endPoint = peerConnection.GrpcEndPoints[0];

        // Use the endpoint as the client key, not the peer ID
        var clientKey = $"{endPoint.EndPoint.Host}:{endPoint.EndPoint.Port}";
        var client = GetOrCreateClient(clientKey, endPoint);

        try
        {
            var request = new DeliverOpaqueMessageRequest
            {
                Version = 1,
                Payload = ByteString.CopyFrom(message.Value)
            };

            // Add diagnostic logging for the payload
            _logger.LogInformation("Sending message payload with hash: {PayloadHash}, length: {PayloadLength}",
                Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(message.Value)),
                message.Value.Length);
                
            _logger.LogInformation("Sending message to {RecipientPeerId} for conversation {ConversationId}",
                recipientPeerId, directSessionId);
            var response = await client.DeliverOpaqueMessageAsync(request, cancellationToken: cancellationToken);
            _logger.LogInformation("Message sent successfully to {RecipientPeerId}. Response version: {Version}",
                recipientPeerId, response.Version);

            peerConnection.UpdateLastSeen(endPoint, DateTime.UtcNow);
            await _peerConnectionRepository.SaveAsync(peerConnection);
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