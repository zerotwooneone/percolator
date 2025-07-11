using Grpc.Net.Client;
using Percolator.Contracts;
using Percolator.Cryptography;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Percolator.Identity;
using IdentityPeerId = Percolator.Identity.PeerId;
using System.Net.Security;
using Percolator.Network;
using NetworkPeerId = Percolator.Network.PeerId;
using Percolator.Chat.ValueObjects;

namespace Percolator.Application.Network;

public class GrpcMessageTransportService : IMessageTransportService
{
    private readonly ConcurrentDictionary<IdentityPeerId, TransportService.TransportServiceClient> _clients = new();
    private readonly ILogger<GrpcMessageTransportService> _logger;
    private readonly IPeerRepository _peerRepository;
    private readonly IPeerConnectionRepository _peerConnectionRepository;

    public GrpcMessageTransportService(ILogger<GrpcMessageTransportService> logger, IPeerRepository peerRepository, IPeerConnectionRepository peerConnectionRepository)
    {
        _logger = logger;
        _peerRepository = peerRepository;
        _peerConnectionRepository = peerConnectionRepository;
    }

    public async Task SendMessageAsync(IdentityPeerId recipientPeerId, ConversationId conversationId, RatchetMessage message)
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
        

        var client = GetOrCreateClient(peer, endPoint);

        try
        {
            var request = new DeliverOpaqueMessageRequest
            {
                SessionId = conversationId.Value.ToString(),
                Payload = Google.Protobuf.ByteString.CopyFrom(message.Ciphertext.Value)
            };

            _logger.LogInformation("Sending message to {RecipientPeerId} for conversation {ConversationId}", recipientPeerId, conversationId);
            var response = await client.DeliverOpaqueMessageAsync(request);
            _logger.LogInformation("Message sent successfully to {RecipientPeerId}. Response version: {Version}", recipientPeerId, response.Version);
            
            peerConnection.UpdateLastSeen(endPoint, DateTime.UtcNow);
            //todo: figure out if we can get the client's TLS certificate and store it in the peer connection
            await _peerConnectionRepository.SaveAsync(peerConnection);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send message to {RecipientPeerId} for conversation {ConversationId}", recipientPeerId, conversationId);
            throw;
        }
    }

    private TransportService.TransportServiceClient GetOrCreateClient(Peer peer, GrpcEndPoint endPoint)
    {
        return _clients.GetOrAdd(peer.Id, _ =>
        {
            var handler = new HttpClientHandler();
            handler.ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
            {
                if (cert is null)
                {
                    _logger.LogWarning("TLS validation failed for peer {PeerId}: No certificate was presented.", peer.Id);
                    return false;
                }

                // Allow name mismatch since we connect via IP, but fail on other errors like expiration.
                if (errors != SslPolicyErrors.None && errors != SslPolicyErrors.RemoteCertificateNameMismatch)
                {
                    _logger.LogWarning("TLS validation failed for peer {PeerId} with policy errors: {SslPolicyErrors}", peer.Id, errors);
                    return false;
                }
                var networkPeerId = new NetworkPeerId(peer.Id.Value);
                // Block to perform async operation in sync callback. This is a known issue with this API.
                var peerConnection = _peerConnectionRepository.GetByIdAsync(networkPeerId).GetAwaiter().GetResult();
                var presentedCertRaw = cert.RawData;

                if (peerConnection is null || !peerConnection.TlsCertificates.Any())
                {
                    // Trust on First Use (TOFU): No certificates are stored for this peer, so trust this one.
                    _logger.LogInformation("First connection to peer {PeerId}. Trusting and storing TLS certificate.", peer.Id);

                    var newConnection = new PeerConnection(
                        networkPeerId,
                        null,
                        new List<GrpcEndPoint> { endPoint },
                        new List<TlsCertificate> { new(presentedCertRaw) },
                        DateTimeOffset.UtcNow);

                    _peerConnectionRepository.SaveAsync(newConnection).GetAwaiter().GetResult();
                    return true;
                }

                // Certificate Pinning: Verify the presented certificate against our stored list.
                foreach (var storedCert in peerConnection.TlsCertificates)
                {
                    if (storedCert.RawData.SequenceEqual(presentedCertRaw))
                    {
                        _logger.LogDebug("Successfully validated known TLS certificate for peer {PeerId}", peer.Id);
                        return true; // Match found, connection is trusted.
                    }
                }

                _logger.LogWarning("TLS validation failed for peer {PeerId}. Presented certificate does not match any stored certificate. Possible MITM attack.", peer.Id);
                return false; // No match found, connection is untrusted.
            };

            var address = $"https://{endPoint.EndPoint.Address}:{endPoint.EndPoint.Port}";
            var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = handler });
            return new TransportService.TransportServiceClient(channel);
        });
    }
}