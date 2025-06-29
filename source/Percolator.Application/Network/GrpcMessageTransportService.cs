using System;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Sessions;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Percolator.Application.Sessions;

namespace Percolator.Application.Network
{
    public class GrpcMessageTransportService : IMessageTransportService
    {
        private readonly ILogger<GrpcMessageTransportService> _logger;
        private readonly DirectSessionManager _sessionManager;

        // This will be used to map PeerId to gRPC channel/client
        private readonly ConcurrentDictionary<PeerId, TransportService.TransportServiceClient> _clients = new();

        public GrpcMessageTransportService(ILogger<GrpcMessageTransportService> logger, DirectSessionManager sessionManager)
        {
            _logger = logger;
            _sessionManager = sessionManager;
        }

        public async Task SendMessageAsync(PeerId recipientPeerId, ConversationId conversationId, RatchetMessage message)
        {
            // In a real application, you would need a way to resolve the recipientPeerId to a network address (IP/Port).
            // For this self-test, we'll assume a direct connection or a known address.
            // This part needs to be integrated with Percolator.Network's peer discovery.
            // For now, we'll use a placeholder URL.
            var recipientAddress = "http://localhost:5000"; // Placeholder: Needs dynamic resolution

            try
            {
                var client = GetOrCreateClient(recipientPeerId, recipientAddress);

                var request = new DeliverOpaqueMessageRequest
                {
                    SessionId = conversationId.Value.ToString(),
                    Payload = Google.Protobuf.ByteString.CopyFrom(message.Ciphertext)
                };

                _logger.LogInformation("Sending message to {RecipientPeerId} for conversation {ConversationId}", recipientPeerId, conversationId);
                var response = await client.DeliverOpaqueMessageAsync(request);
                _logger.LogInformation("Message sent successfully to {RecipientPeerId}. Response version: {Version}", recipientPeerId, response.Version);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send message to {RecipientPeerId} for conversation {ConversationId}", recipientPeerId, conversationId);
                throw; // Re-throw to adhere to fail-forward policy
            }
        }

        private TransportService.TransportServiceClient GetOrCreateClient(PeerId peerId, string address)
        {
            return _clients.GetOrAdd(peerId, _ =>
            {
                // For testing, allow insecure HTTP. In production, this MUST be HTTPS with proper cert validation.
                var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
                {
                    Credentials = Grpc.Core.ChannelCredentials.Insecure // DANGER: For testing only
                });
                return new TransportService.TransportServiceClient(channel);
            });
        }

        // This class will also need to expose a gRPC service endpoint for incoming messages.
        // This will be handled by a separate gRPC service implementation that calls DirectSessionManager.ReceiveMessageAsync
    }
}