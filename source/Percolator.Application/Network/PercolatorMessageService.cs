using System.Net;
using System.Text.Json;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Percolator.Application.KeyExchange;
using Percolator.Application.Sessions;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Chat;
using ChatConversation = Percolator.Chat.Conversation;
using ChatConversationId = Percolator.Chat.ValueObjects.ConversationId;
using ChatParticipantId = Percolator.Chat.ValueObjects.ParticipantId;
using SessionPeerId = Percolator.Sessions.PeerId;
using SessionConversationId = Percolator.Sessions.ConversationId;
using Percolator.Application.Identity;
using System.Security.Cryptography;
using Percolator.Identity;
using Percolator.Network;
using NetworkPeerId = Percolator.Network.PeerId;
using IdentityPeer = Percolator.Identity.Peer;
using IdentityPeerId = Percolator.Identity.PeerId;
using Percolator.Chat.ValueObjects;
using Percolator.Sessions;
using SessionSharedSecret = Percolator.Sessions.SharedSecret;
using SessionRatchetMessage = Percolator.Sessions.RatchetMessage;
using SessionRatchetIdentityKey = Percolator.Sessions.RatchetIdentityKey;

namespace Percolator.Application.Network
{
    public class PercolatorMessageService : TransportService.TransportServiceBase
    {
        private readonly ILogger<PercolatorMessageService> _logger;
        private readonly ActiveIdentityContext _activeIdentityContext;
        private readonly X3DHOrchestrator _x3dhOrchestrator;
        private readonly IDirectSessionManager _sessionManager;
        private readonly IConversationRepository _conversationRepository;
        private readonly IPeerRepository _peerRepository;
        private readonly IPeerConnectionRepository _peerConnectionRepository;

        public PercolatorMessageService(ILogger<PercolatorMessageService> logger, ActiveIdentityContext activeIdentityContext, X3DHOrchestrator x3dhOrchestrator, IDirectSessionManager sessionManager, IConversationRepository conversationRepository, IPeerRepository peerRepository, IPeerConnectionRepository peerConnectionRepository)
        {
            _logger = logger;
            _activeIdentityContext = activeIdentityContext;
            _x3dhOrchestrator = x3dhOrchestrator;
            _sessionManager = sessionManager;
            _conversationRepository = conversationRepository;
            _peerRepository = peerRepository;
            _peerConnectionRepository = peerConnectionRepository;
        }

        public override async Task<EstablishSessionResponse> EstablishSession(EstablishSessionRequest request, ServerCallContext context)
        {
            _logger.LogInformation("EstablishSession invoked by peer {Peer}", context.Peer);

            var clientCertificate = await context.GetHttpContext().Connection.GetClientCertificateAsync();
            if (clientCertificate is null)
            {
                _logger.LogError("Handshake failed: Client did not provide a certificate.");
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Client certificate is required."));
            }
            
            _logger.LogInformation("Client certificate provided. Subject: {Subject}, Issuer: {Issuer}", clientCertificate.Subject, clientCertificate.Issuer);

            try
            {
                //The client certificate's public key is used as the peer's identity key.
                _logger.LogInformation("Received request to establish a new session.");
                if (_activeIdentityContext.Identity is null)
                {
                    _logger.LogError("Local peer identity has not been established. Cannot respond to handshake.");
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, "Server identity not initialized."));
                }

                var handshakeResult = _x3dhOrchestrator.ProcessHandshake(request.InitiatorBundle, request.InitiatorEphemeralKey.ToByteArray());

                // Look up the peer by their public identity agreement key.
                var ideneityAgreementKeyBytes = request.InitiatorBundle.IdentityAgreementKey.ToByteArray();
                var directMessagePublicKey = new DirectMessagePublicKey(ideneityAgreementKeyBytes);
                var connnectionInfo =
                    await _peerConnectionRepository.GetByDirectMessage(directMessagePublicKey);
                
                var timestamp = DateTimeOffset.Now;
                
                //todo: this is not the correct peer endpoint. we need to send the endpoint from the peer
                var dnsEndpointParts = context.Peer.Split(':').Skip(1).ToArray();
                if (dnsEndpointParts.Length != 2)
                {
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, $"Invalid endpoint: {context.Peer}"));
                }
                if(!int.TryParse(dnsEndpointParts[1], out var port) || port <= 0 || port > 65535) 
                {
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, "Invalid port."));
                }
                var ipEndPoint = new DnsEndPoint(dnsEndpointParts[0], port);
                
                if (connnectionInfo is null)
                {
                    _logger.LogWarning("No connection info found for peer {directMessagePublicKey}. Creating a new connection record.", directMessagePublicKey);
                    connnectionInfo = new PeerConnection(
                        new NetworkPeerId(Guid.NewGuid()),
                        directMessagePublicKey,
                        [new GrpcEndPoint(ipEndPoint, timestamp)],
                        new List<TlsCertificate>(),
                        timestamp);
                }
                else
                {
                    var grpcEndPoint = connnectionInfo.GrpcEndPoints.FirstOrDefault(e => e.EndPoint.Equals(ipEndPoint));
                    if (grpcEndPoint is null)
                    {
                        _logger.LogWarning("No gRPC endpoints found for peer {directMessagePublicKey}. Adding a new one.", directMessagePublicKey);
                        connnectionInfo.AddGrpcEndPoint(new GrpcEndPoint(ipEndPoint, timestamp));
                    }
                    else
                    {
                        connnectionInfo.UpdateLastSeen(grpcEndPoint,timestamp);
                    }
                }
                await _peerConnectionRepository.SaveAsync(connnectionInfo);
                var networkPeerId = new NetworkPeerId(connnectionInfo.Id.Value);
                
                
                var peer = await _peerRepository.GetByIdAsync(new IdentityPeerId(networkPeerId.Value));

                // If the peer is unknown, create a new record for them.
                if (peer is null)
                {
                    var publicKeyHash = new PublicKeyHash(SHA1.HashData(ideneityAgreementKeyBytes));
                    _logger.LogInformation("Peer with key hash {KeyHash} is unknown. Creating a new peer record.", publicKeyHash);
                    // For now, we'll auto-generate a name.
                    var newPeerName = $"Peer-{publicKeyHash.ToString().Substring(0, 8)}";
                    
                    peer = new IdentityPeer(new IdentityPeerId(connnectionInfo.Id.Value), newPeerName);
                    await _peerRepository.AddAsync(peer);
                }
                
                var channelId = new ChannelId(directMessagePublicKey.Value);
                var conversation = await _conversationRepository.GetByChannelIdAsync(channelId);

                if (conversation is null)
                {
                    var participants = new List<ChatParticipantId>
                    {
                        new(_activeIdentityContext.Identity.Id),
                        new(peer.Id.Value)
                    };

                    conversation = new ChatConversation(
                        ChatConversationId.NewId(),
                        channelId,
                        participants,
                        new List<Message>(),
                        peer.Name);

                    await _conversationRepository.AddAsync(conversation);
                    _logger.LogInformation("Created new conversation with {PeerName} for channel {ChannelId}", peer.Name, channelId);
                }

                await _sessionManager.EstablishSessionAsResponderAsync(
                    new SessionConversationId(conversation.Id.Value),
                    new SessionPeerId(peer.Id.Value),
                    new SessionIdentityKey(request.InitiatorBundle.IdentityAgreementKey.ToByteArray()),
                    new SessionSharedSecret(handshakeResult.SharedSecret.Value));

                _logger.LogInformation("Successfully established session {SessionId} with peer {PeerId}", conversation.Id, peer.Id);

                return new EstablishSessionResponse
                {
                    SessionId = conversation.Id.Value.ToString(),
                    ResponderBundle = handshakeResult.ResponderBundle
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to establish session.");
                throw new RpcException(new Status(StatusCode.Internal, "Session establishment failed."));
            }
        }

        public override async Task<DeliverOpaqueMessageResponse> DeliverOpaqueMessage(DeliverOpaqueMessageRequest request, ServerCallContext context)
        {
            _logger.LogInformation("Received opaque message for session {SessionId}", request.SessionId);

            try
            {
                var conversationId = new SessionConversationId(Guid.Parse(request.SessionId));

                // The payload is a JSON-serialized RatchetMessage
                var ratchetMessage = JsonSerializer.Deserialize<SessionRatchetMessage>(request.Payload.ToByteArray());
                if (ratchetMessage is null)
                {
                    throw new InvalidOperationException("Failed to deserialize RatchetMessage.");
                }

                // Decrypt the message to get the Protobuf-serialized InternalEnvelope
                var plaintext = await _sessionManager.ReceiveMessageAsync(conversationId, ratchetMessage);
                if (plaintext is null)
                {
                    _logger.LogWarning("Decryption resulted in null plaintext for session {SessionId}. This may be a skipped message.", request.SessionId);
                    return new DeliverOpaqueMessageResponse { Version = 1 }; // Acknowledge receipt
                }

                // Deserialize the InternalEnvelope
                var internalEnvelope = InternalEnvelope.Parser.ParseFrom(plaintext.Value);

                // Dispatch based on the application payload
                switch (internalEnvelope.ApplicationPayloadCase)
                {
                    case InternalEnvelope.ApplicationPayloadOneofCase.ChatEnvelope:
                        HandleChatEnvelope(internalEnvelope.ChatEnvelope);
                        break;
                    // Other cases like FileShare, Dht, etc., would be handled here.
                    default:
                        _logger.LogWarning("Received unhandled application payload type: {PayloadType}", internalEnvelope.ApplicationPayloadCase);
                        break;
                }


                return new DeliverOpaqueMessageResponse { Version = 1 };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing opaque message for session {SessionId}", request.SessionId);
                throw new RpcException(new Status(StatusCode.Internal, "Error processing message."));
            }
        }

        private void HandleChatEnvelope(ChatEnvelope chatEnvelope)
        {
            switch (chatEnvelope.MessageCase)
            {
                case ChatEnvelope.MessageOneofCase.TextMessage:
                    _logger.LogInformation("Received Text Message: {Content}", chatEnvelope.TextMessage.Content);
                    // Here you would typically publish a MediatR notification
                    // for another service to handle the text message.
                    break;
                // Handle other chat message types
                default:
                    _logger.LogWarning("Received unhandled chat message type: {MessageType}", chatEnvelope.MessageCase);
                    break;
            }
        }
    }
}