using System.Net;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Percolator.Application.KeyExchange;
using Percolator.Application.Sessions;
using Percolator.Contracts;
using Percolator.Chat;
using ChatConversation = Percolator.Chat.Conversation;
using ChatConversationId = Percolator.Chat.ValueObjects.ConversationId;
using ChatParticipantId = Percolator.Chat.ValueObjects.ParticipantId;
using Percolator.Application.Identity;
using System.Security.Cryptography;
using Google.Protobuf;
using Percolator.Identity;
using Percolator.Network;
using NetworkPeerId = Percolator.Network.PeerId;
using IdentityPeer = Percolator.Identity.Peer;
using IdentityPeerId = Percolator.Identity.PeerId;
using Percolator.Chat.ValueObjects;
using Percolator.Cryptography;
using PreKeyBundle = Percolator.Contracts.PreKeyBundle;

namespace Percolator.Application.Network
{
    public class PercolatorMessageService : TransportService.TransportServiceBase
    {
        private readonly ILogger<PercolatorMessageService> _logger;
        private readonly ActiveIdentityContext _activeIdentityContext;
        private readonly IX3DHOrchestrator _x3dhOrchestrator;
        private readonly IDirectSessionManager _sessionManager;
        private readonly IConversationRepository _conversationRepository;
        private readonly IPeerRepository _peerRepository;
        private readonly IPeerConnectionRepository _peerConnectionRepository;
        private readonly IX3DHManager _x3DhManager;

        public PercolatorMessageService(
            ILogger<PercolatorMessageService> logger, 
            ActiveIdentityContext activeIdentityContext, 
            IX3DHOrchestrator x3dhOrchestrator, 
            IDirectSessionManager sessionManager, 
            IConversationRepository conversationRepository, 
            IPeerRepository peerRepository, 
            IPeerConnectionRepository peerConnectionRepository, 
            IX3DHManager x3DhManager)
        {
            _logger = logger;
            _activeIdentityContext = activeIdentityContext;
            _x3dhOrchestrator = x3dhOrchestrator;
            _sessionManager = sessionManager;
            _conversationRepository = conversationRepository;
            _peerRepository = peerRepository;
            _peerConnectionRepository = peerConnectionRepository;
            _x3DhManager = x3DhManager;
        }

        public override async Task<EstablishSessionResponse> EstablishSession(EstablishSessionRequest request, ServerCallContext context)
        {
            _logger.LogInformation("EstablishSession invoked by peer {Peer}", context.Peer);

            var clientCertificate = await context.GetHttpContext().Connection.GetClientCertificateAsync();
            if (clientCertificate is null)
            {
                _logger.LogWarning("Handshake failed: Client did not provide a certificate...");
                //throw new RpcException(new Status(StatusCode.PermissionDenied, "Client certificate is required."));
            }
            
            _logger.LogInformation("Client certificate provided. Subject: {Subject}, Issuer: {Issuer}", clientCertificate?.Subject, clientCertificate?.Issuer);

            try
            {
                //The client certificate's public key is used as the peer's identity key.
                _logger.LogInformation("Received request to establish a new session.");
                if (_activeIdentityContext.Identity is null)
                {
                    _logger.LogError("Local peer identity has not been established. Cannot respond to handshake.");
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, "Server identity not initialized."));
                }

                // Log details about the initiator bundle to debug prekey signature issues
                _logger.LogInformation("Processing X3DH handshake with initiator bundle. Examining bundle properties...");

                var ephemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                
                var sharedSecret = _x3dhOrchestrator.InitiateHandshake(request.InitiatorBundle, ephemeralKey);
                _logger.LogInformation("X3DH handshake processed successfully as Initiator.");

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
                _logger.LogInformation("Connection info saved for peer {networkPeerId}", networkPeerId.Value);
                
                var peer = await _peerRepository.GetByIdAsync(new IdentityPeerId(networkPeerId.Value));

                // If the peer is unknown, create a new record for them.
                if (peer is null)
                {
                    var publicKeyHash = new PublicKeyHash(SHA1.HashData(ideneityAgreementKeyBytes));
                    _logger.LogInformation("Peer with key hash {KeyHash} is unknown. Creating a new peer record", Convert.ToBase64String(publicKeyHash.Value));
                    // For now, we'll auto-generate a name.
                    var newPeerName = $"Peer-{Convert.ToBase64String(publicKeyHash.Value)}";
                    
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
                    
                    _logger.LogInformation("Creating new conversation with participants  {Participants}", string.Join(", ", participants));

                    conversation = new ChatConversation(
                        ChatConversationId.NewId(),
                        channelId,
                        participants,
                        new List<Message>(),
                        peer.Name);

                    await _conversationRepository.AddAsync(conversation);
                    _logger.LogInformation("Created new conversation with peer {PeerName}", peer.Name);
                    
                    if (_logger.IsEnabled(LogLevel.Warning))
                    {
                        var testConversation = await _conversationRepository.GetByChannelIdAsync(channelId);
                        _logger.LogWarning("Retrieved conversation with participants {Participants} ", string.Join(", ", testConversation!.Participants));
                    }
                    
                    await _sessionManager.EstablishSessionAsInitiatorAsync(
                        new SessionId(conversation.Id.Value),
                        new IdentityPeerId(peer.Id.Value),
                        new RatchetIdentityKey(request.InitiatorBundle.IdentityAgreementKey.ToByteArray()),
                        new RatchetEphemeralKey(request.InitiatorBundle.SignedPreKey.ToByteArray()),
                        sharedSecret, 
                        ephemeralKey);
                    _logger.LogInformation("Successfully established session {SessionId} with peer {PeerId}", conversation.Id, peer.Id);
                }
                
                var responseBundle = new PreKeyBundle
                {
                    // Your Identity Keys
                    IdentitySigningKey = ByteString.CopyFrom(_activeIdentityContext.Keys.IdentitySigningKey.ExportSubjectPublicKeyInfo()),
                    IdentityAgreementKey = ByteString.CopyFrom(_activeIdentityContext.Keys.IdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo()),

                    // Your new Ephemeral Key for this session. We can place it in the SignedPreKey field.
                    SignedPreKey = ByteString.CopyFrom(ephemeralKey.PublicKey.ExportSubjectPublicKeyInfo()),

                    // You must sign the key you are sending.
                    PreKeySignature = ByteString.CopyFrom(_x3DhManager.SignPreKey(
                        _activeIdentityContext.Keys.IdentitySigningKey,
                        new PreKey(ephemeralKey.PublicKey.ExportSubjectPublicKeyInfo())
                    ).Value)
                };
                
                return new EstablishSessionResponse
                {
                    SessionId = conversation.Id.Value.ToString(),
                    ResponderBundle = responseBundle
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
            _logger.LogWarning("Received opaque message for session {SessionId} as {PeerName}:{PeerId}", request.SessionId, _activeIdentityContext.Identity?.Name, _activeIdentityContext.Identity?.Id);

            try
            {
                var conversationId = new SessionId(Guid.Parse(request.SessionId));

                // Create a SessionRatchetMessage from the payload bytes
                var payload = request.Payload.ToByteArray();
                var sessionRatchetMessage = new SessionRatchetMessage(payload);

                // Add diagnostic logging for the received payload
                _logger.LogInformation("Received message payload with hash: {PayloadHash}, length: {PayloadLength}",
                    Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(payload)),
                    payload.Length);

                // Optional: Check version if needed
                try 
                {
                    var protoMessage = Contracts.RatchetMessage.Parser.ParseFrom(request.Payload);
                    if (protoMessage.Version > 1)
                    {
                        _logger.LogWarning("Received message with newer version {Version} than supported (1)", 
                            protoMessage.Version);
                    }
                }
                catch (Google.Protobuf.InvalidProtocolBufferException ex)
                {
                    _logger.LogWarning(ex, "Could not parse protobuf RatchetMessage from payload - may be using legacy format");
                    // Continue with the SessionRatchetMessage we already created
                }

                // Decrypt the message to get the Protobuf-serialized InternalEnvelope
                var plaintext = await _sessionManager.ReceiveMessageAsync(conversationId, sessionRatchetMessage);
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
                _logger.LogError(ex, "Error processing opaque message for session {SessionId} - ex:{Exception}", request.SessionId, ex);
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