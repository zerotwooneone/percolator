using Grpc.Net.Client;
using Percolator.Contracts;
using Percolator.Cryptography;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Percolator.Identity;
using ConversationId = Percolator.Chat.ValueObjects.ConversationId;

namespace Percolator.Application.Network;

public class GrpcMessageTransportService : IMessageTransportService
{
    private readonly ILogger<GrpcMessageTransportService> _logger;
    private readonly IPeerRepository _peerRepository;

    private readonly ConcurrentDictionary<PeerId, TransportService.TransportServiceClient> _clients = new();

    public GrpcMessageTransportService(ILogger<GrpcMessageTransportService> logger, IPeerRepository peerRepository)
    {
        _logger = logger;
        _peerRepository = peerRepository;
    }

    public async Task SendMessageAsync(PeerId recipientPeerId, ConversationId conversationId, RatchetMessage message)
    {
        var peer = await _peerRepository.GetByIdAsync(recipientPeerId);
        if (peer is null)
        {
            _logger.LogError("Could not find peer with ID {PeerId}", recipientPeerId);
            return;
        }

        var recipientAddress = $"https://{peer.IpAddress}:{peer.GrpcEndpoint.Port}";

        try
        {
            var client = GetOrCreateClient(peer, recipientAddress);

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
            throw;
        }
    }

    private TransportService.TransportServiceClient GetOrCreateClient(Peer peer, string address)
    {
        return _clients.GetOrAdd(peer.Id, _ =>
        {
            var handler = new HttpClientHandler();
            handler.ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
            {
                if (cert is null)
                {
                    return false;
                }

                var serverCertThumbprint = cert.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);
                return string.Equals(serverCertThumbprint, peer.Thumbprint, StringComparison.OrdinalIgnoreCase);
            };

            var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = handler });
            return new TransportService.TransportServiceClient(channel);
        });
    }
}