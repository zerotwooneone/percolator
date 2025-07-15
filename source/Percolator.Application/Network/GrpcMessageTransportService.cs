using Grpc.Net.Client;
using Percolator.Contracts;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Percolator.Identity;
using IdentityPeerId = Percolator.Identity.PeerId;
using System.Net.Security;
using Percolator.Network;
using NetworkPeerId = Percolator.Network.PeerId;
using Percolator.Chat.ValueObjects;
using SessionRatchetMessage = Percolator.Sessions.RatchetMessage;
using System;

namespace Percolator.Application.Network;

public class GrpcMessageTransportService : IMessageTransportService
{
    private readonly ConcurrentDictionary<string, TransportService.TransportServiceClient> _clients = new();
    private readonly ILogger<GrpcMessageTransportService> _logger;
    private readonly IPeerRepository _peerRepository;
    private readonly IPeerConnectionRepository _peerConnectionRepository;
    private readonly SharedCertificateManager _certificateManager;

    public GrpcMessageTransportService(
        ILogger<GrpcMessageTransportService> logger,
        IPeerRepository peerRepository,
        IPeerConnectionRepository peerConnectionRepository,
        SharedCertificateManager certificateManager)
    {
        _logger = logger;
        _peerRepository = peerRepository;
        _peerConnectionRepository = peerConnectionRepository;
        _certificateManager = certificateManager;
    }

    public async Task SendMessageAsync(IdentityPeerId recipientPeerId, ConversationId conversationId,
        SessionRatchetMessage message)
    {
        var peer = await _peerRepository.GetByIdAsync(recipientPeerId);
        if (peer is null)
        {
            _logger.LogError("Could not find peer with ID {PeerId}", recipientPeerId);
            return;
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

        //todo: we should loop over all the connections and try to send the message to all of them sequentially
        var endPoint = peerConnection.GrpcEndPoints[0];

        // Use the endpoint as the client key, not the peer ID
        var clientKey = $"{endPoint.EndPoint.Host}:{endPoint.EndPoint.Port}";
        var client = GetOrCreateClient(clientKey, endPoint);

        try
        {
            var request = new DeliverOpaqueMessageRequest
            {
                SessionId = conversationId.Value.ToString(),
                Payload = Google.Protobuf.ByteString.CopyFrom(message.Value)
            };

            _logger.LogInformation("Sending message to {RecipientPeerId} for conversation {ConversationId}",
                recipientPeerId, conversationId);
            var response = await client.DeliverOpaqueMessageAsync(request);
            _logger.LogInformation("Message sent successfully to {RecipientPeerId}. Response version: {Version}",
                recipientPeerId, response.Version);

            peerConnection.UpdateLastSeen(endPoint, DateTime.UtcNow);
            await _peerConnectionRepository.SaveAsync(peerConnection);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send message to {RecipientPeerId} for conversation {ConversationId}",
                recipientPeerId, conversationId);
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

                // For local connections, use a simple handler without any TLS configuration
                _logger.LogInformation("Using HTTP/2 without TLS for local connection: {Endpoint}", endPoint);
                var handler = new SocketsHttpHandler
                {
                    EnableMultipleHttp2Connections = true,
                    KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
                    KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                    KeepAlivePingTimeout = TimeSpan.FromSeconds(30)
                };


                // Create HTTP client and configure channel options
                var httpClient = new HttpClient(handler);

                var channelOptions = new GrpcChannelOptions
                {
                    HttpClient = httpClient,
                    MaxReceiveMessageSize = 4 * 1024 * 1024, // 4 MB
                    MaxSendMessageSize = 4 * 1024 * 1024 // 4 MB
                };

                var uri = new Uri($"http://{endPoint.EndPoint.Host}:{endPoint.EndPoint.Port}");
                _logger.LogInformation("Creating gRPC channel to {Uri} using http", uri);

                var channel = GrpcChannel.ForAddress(uri, channelOptions);
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