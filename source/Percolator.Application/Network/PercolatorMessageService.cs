using System.Net;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using MediatR;
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
using Google.Protobuf.WellKnownTypes;
using Percolator.Identity;
using Percolator.Network;
using NetworkPeerId = Percolator.Network.PeerId;
using IdentityPeer = Percolator.Identity.Peer;
using IdentityPeerId = Percolator.Identity.PeerId;
using Percolator.Chat.ValueObjects;
using Percolator.Cryptography;
using CryptoSignature = Percolator.Cryptography.Signature;
using PreKeyBundle = Percolator.Contracts.PreKeyBundle;
using Percolator.Cryptography.Primitives;
using ISigningService = Percolator.Cryptography.ISigningService;
using PublicKey = Percolator.Cryptography.PublicKey;

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
        private readonly IPreKeyBundleRepository _bundleRepository;
        private readonly ISigningService _signingService;
        private readonly IPeerTrustManager _peerTrustManager;
        private readonly IMediator _mediator;

        public PercolatorMessageService(
            ILogger<PercolatorMessageService> logger, 
            ActiveIdentityContext activeIdentityContext, 
            IX3DHOrchestrator x3dhOrchestrator, 
            IDirectSessionManager sessionManager, 
            IConversationRepository conversationRepository, 
            IPeerRepository peerRepository, 
            IPeerConnectionRepository peerConnectionRepository, 
            IX3DHManager x3DhManager,
            IPreKeyBundleRepository bundleRepository,
            ISigningService signingService,
            IPeerTrustManager peerTrustManager,
            IMediator mediator)
        {
            _logger = logger;
            _activeIdentityContext = activeIdentityContext;
            _x3dhOrchestrator = x3dhOrchestrator;
            _sessionManager = sessionManager;
            _conversationRepository = conversationRepository;
            _peerRepository = peerRepository;
            _peerConnectionRepository = peerConnectionRepository;
            _x3DhManager = x3DhManager;
            _bundleRepository = bundleRepository;
            _signingService = signingService;
            _peerTrustManager = peerTrustManager;
            _mediator = mediator;
        }

        public override async Task<EstablishDirectSessionResponse> EstablishDirectSession(EstablishDirectSessionRequest request, ServerCallContext context)
        {
            var payload = EstablishDirectSessionRequest.Types.DirectInitiatorPayload.Parser.ParseFrom(request.InitiatorBundle.SignedPayload);
            if(!payload.HasCallbackPort || payload.CallbackPort < 1024 || payload.CallbackPort > 65535)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, $"Invalid callback port: {payload.CallbackPort}"));
            }
            uint callbackPort = payload.CallbackPort;
            
            var remoteIdentitySigningKeyBytes = request.InitiatorBundle.IdentitySigningKey.ToByteArray();
            var networkIdentitySigningKey = new DirectMessagePublicKey(remoteIdentitySigningKeyBytes);
            var peerConnectionInfo =
                await _peerConnectionRepository.GetByDirectMessage(networkIdentitySigningKey);
            var timestamp = DateTimeOffset.Now;
            
            var peerGrpcEnpointParts = context.Peer.Split(':').Skip(1).ToArray();
            if (peerGrpcEnpointParts.Length != 2)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, $"Invalid endpoint: {context.Peer}"));
            }
            var peerEndPoint = new DnsEndPoint(peerGrpcEnpointParts[0], (int)callbackPort);
                
            if (peerConnectionInfo is null)
            {
                _logger.LogWarning("No connection info found for peer {DirectMessagePublicKey}. Creating a new connection record", Convert.ToBase64String(networkIdentitySigningKey.Value));
                
                var grpcEndPoint = new GrpcEndPoint(peerEndPoint, timestamp);
                peerConnectionInfo = new PeerConnection(
                    new NetworkPeerId(Guid.NewGuid()),
                    networkIdentitySigningKey,
                    grpcEndPoint is null ? [] : new[] { grpcEndPoint },
                    new List<TlsCertificate>(),
                    timestamp);
            }
            else
            {
                var grpcEndPoint = peerConnectionInfo.GrpcEndPoints.FirstOrDefault(e => e.EndPoint.Equals(peerEndPoint));
                if (grpcEndPoint is null)
                {
                    _logger.LogWarning("No gRPC endpoints found for peer {DirectMessagePublicKey}. Adding a new one", Convert.ToBase64String(networkIdentitySigningKey.Value));
                    peerConnectionInfo.AddGrpcEndPoint(new GrpcEndPoint(peerEndPoint, timestamp));
                }
                else
                {
                    peerConnectionInfo.UpdateLastSeen(grpcEndPoint,timestamp);
                }
            }
            await _peerConnectionRepository.SaveAsync(peerConnectionInfo);
            
            return await Inner_EstablishSessionResponse(
                request, 
                context,
                GetPreKeyFromRequestPayload);
            
            byte[] GetPreKeyFromRequestPayload(byte[] _)
            {
                return payload.SignedPreKey.ToByteArray();
            }
        }

        private async Task<EstablishDirectSessionResponse> Inner_EstablishSessionResponse(
            EstablishDirectSessionRequest request, 
            ServerCallContext context,
            Func<byte[], byte[]> getPreKeyFromRequestPayload)
        {
            _logger.LogInformation("EstablishSession invoked by peer {Peer}", context.Peer);

            if (request.InitiatorBundle is null)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Initiator bundle is required."));
            }

            if (!request.InitiatorBundle.HasIdentitySigningKey)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Initiator bundle must contain identity signing key."));
            }

            if (!request.InitiatorBundle.HasIdentityAgreementKey)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Initiator bundle must contain identity agreement key."));
            }

            if (!request.InitiatorBundle.HasSignedPayload)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Initiator bundle must contain signed payload."));
            }

            if (!request.InitiatorBundle.HasPayloadSignature)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Initiator bundle must contain payload signature."));
            }
            var remoteIdentityKey = new RatchetIdentityKey(request.InitiatorBundle.IdentitySigningKey.ToByteArray());
            var requestPayloadSignature = new CryptoSignature(request.InitiatorBundle.PayloadSignature.ToByteArray());
            var requestPayload = new PreKey(request.InitiatorBundle.SignedPayload.ToByteArray());
            if (!_x3DhManager.VerifySignature(
                    remoteIdentityKey,
                    requestPayload, 
                    requestPayloadSignature))
            {
                throw new CryptographicException("Invalid signature on signed pre-key.");
            }

            _logger.LogDebug("Signature verification successful");
            var clientCertificate = await context.GetHttpContext().Connection.GetClientCertificateAsync();
            if (clientCertificate is null)
            {
                _logger.LogWarning("Client did not provide a certificate...");
                //throw new RpcException(new Status(StatusCode.PermissionDenied, "Client certificate is required."));
            }
            else
            {
                _logger.LogInformation("Client certificate provided. Subject: {Subject}, Issuer: {Issuer}", clientCertificate.Subject, clientCertificate.Issuer);
            }

            try
            {
                //The client certificate's public key is used as the peer's identity key.
                _logger.LogInformation("Received request to establish a new session");
                if (_activeIdentityContext.Identity is null)
                {
                    _logger.LogError("Local peer identity has not been established. Cannot respond to handshake");
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, "Server identity not initialized."));
                }

                // Log details about the initiator bundle to debug prekey signature issues
                _logger.LogInformation("Processing X3DH handshake with initiator bundle. Examining bundle properties...");

                var ephemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                var preKeyBytes = getPreKeyFromRequestPayload(requestPayload.Value);

                
                var prekeyBundle = new X3dPreKeyBundle(
                    remoteIdentityKey,
                    new RatchetAgreementKey(request.InitiatorBundle.IdentityAgreementKey.ToByteArray()),
                    new PreKey(preKeyBytes),
                    request.InitiatorBundle.HasOneTimePreKey
                        ? new OneTimeKey(request.InitiatorBundle.OneTimePreKey.ToByteArray())
                        : null);
                var sharedSecret = _x3dhOrchestrator.InitiateHandshake(prekeyBundle, ephemeralKey);
                _logger.LogInformation("X3DH handshake processed successfully as Initiator");

                // Look up the peer by their public identity agreement key.
                var remoteIdentitySigningKeyBytes = request.InitiatorBundle.IdentitySigningKey.ToByteArray();
                var networkIdentitySigningKey = new DirectMessagePublicKey(remoteIdentitySigningKeyBytes);
                var connnectionInfo =
                    await _peerConnectionRepository.GetByDirectMessage(networkIdentitySigningKey);

                if (connnectionInfo is null)
                {
                    throw new RpcException(new Status(StatusCode.NotFound, "Peer connection info not found."));
                }

                var networkPeerId = new NetworkPeerId(connnectionInfo.Id.Value);
                _logger.LogInformation("Connection info saved for peer {NetworkPeerId}", networkPeerId.Value);
                
                var peer = await _peerRepository.GetByIdAsync(new IdentityPeerId(networkPeerId.Value));

                // If the peer is unknown, create a new record for them.
                if (peer is null)
                {
                    _logger.LogInformation("Peer with key hash {KeyHash} is unknown. Creating a new peer record", Convert.ToBase64String(remoteIdentitySigningKeyBytes));
                    // For now, we'll auto-generate a name.
                    var newPeerName = $"Peer-{Convert.ToBase64String(remoteIdentitySigningKeyBytes)}";
                    
                    peer = new IdentityPeer(new IdentityPeerId(connnectionInfo.Id.Value), newPeerName);
                    await _peerRepository.AddAsync(peer);
                }
                
                var channelId = new ChannelId(networkIdentitySigningKey.Value);
                var conversation = await _conversationRepository.GetByChannelIdAsync(channelId);

                if (conversation is null)
                {
                    var participants = new List<ChatParticipantId>
                    {
                        new(_activeIdentityContext.Identity.Id),
                        new(peer.Id.Value)
                    };
                    
                    _logger.LogInformation("Creating new conversation with participants  {Participants} channel ID {ChannelId}", string.Join(", ", participants), Convert.ToBase64String(channelId.Value));

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

                    var cryptoSessionId = new SessionId(conversation.Id.Value);
                    
                    await _sessionManager.EstablishSessionAsInitiatorAsync(
                        cryptoSessionId,
                        new IdentityPeerId(peer.Id.Value),
                        remoteIdentityKey,
                        new RatchetEphemeralKey(preKeyBytes),
                        sharedSecret, 
                        ephemeralKey);
                    _logger.LogInformation("Successfully established session {SessionId} with peer {PeerId}", conversation.Id, peer.Id);
                }

                var responsePayload = new EstablishDirectSessionResponse.Types.ResponsePayload
                {
                    EphemeralKey = ByteString.CopyFrom(ephemeralKey.PublicKey.ExportSubjectPublicKeyInfo()),
                    SessionId = conversation.Id.ToString()
                }.ToByteString();
                var signedPayloadBytes = _x3DhManager.SignPreKey(_activeIdentityContext.Keys.IdentitySigningKey,
                    new PreKey(responsePayload.ToByteArray()));
                return new EstablishDirectSessionResponse
                {
                    Response = new EstablishDirectSessionResponse.Types.Response
                    {
                        IdentitySigningKey = ByteString.CopyFrom(_activeIdentityContext.Keys.IdentitySigningKey.ExportSubjectPublicKeyInfo()),
                        ResponsePayload = responsePayload,
                        PayloadSignature = ByteString.CopyFrom(signedPayloadBytes.Value)
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to establish session.");
                throw new RpcException(new Status(StatusCode.Internal, "Session establishment failed."));
            }
        }

        public override async Task<SubmitPreKeyBundleResponse> SubmitPreKeyBundle(SubmitPreKeyBundleRequest request, ServerCallContext context)
        {
            if (!request.HasIdentityKey)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Request must contain an identity key."));
            }
            if(!request.HasSignedPayload)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Request must contain signed payload."));
            }
            if (!request.HasSignature)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Request must contain signature."));
            }

            var identitySigningKeyBytes = request.IdentityKey.ToByteArray();
            if(!_signingService.Verify(
                   request.SignedPayload.ToByteArray(), 
                   new CryptoSignature( request.Signature.ToByteArray()), 
                   new PublicKey(identitySigningKeyBytes)))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid signature."));
            }
            var payload = SubmitPreKeyBundleRequest.Types.PreKeyUploadPayload.Parser.ParseFrom(request.SignedPayload);
            const int maxBundles = 100;
            if (payload.OneTimePreKeys.Count == 0 || payload.OneTimePreKeys.Count > maxBundles)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, $"Request must contain between 1 and {maxBundles} bundles."));
            }
            if (payload.TimestampUtc == null)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Request must contain timestamp."));
            }
            if (payload.ExpiresUtc == null)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Request must contain expiration."));
            }

            if (!payload.SignedPreKey.HasId)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Request must contain signed pre-key ID."));
            }
            var nowTimestamp = DateTimeOffset.Now;
            var timestamp = payload.TimestampUtc.ToDateTimeOffset();
            if (timestamp > nowTimestamp.AddSeconds(1) || timestamp < nowTimestamp.AddSeconds(-30))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Request timestamp is too far in the future or the past."));
            }
            if(payload.ExpiresUtc.ToDateTimeOffset() < timestamp)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Request expiration is before the timestamp."));
            }

            if (!payload.SignedPreKey.HasPublicKey || !payload.SignedPreKey.HasSignature)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Request must contain signed pre-key."));
            }

            var signedPreKeyBytes = payload.SignedPreKey.PublicKey.ToByteArray();
            var signedPreKeySignature = new CryptoSignature(payload.SignedPreKey.Signature.ToByteArray());
            if(!_signingService.Verify(signedPreKeyBytes,
                   signedPreKeySignature,
                   new PublicKey(identitySigningKeyBytes)))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid pre-key signature."));
            }

            // Look up the peer by their public identity agreement key.
            var remoteIdentitySigningKeyBytes = identitySigningKeyBytes;
            var networkIdentitySigningKey = new DirectMessagePublicKey(remoteIdentitySigningKeyBytes);
            var connnectionInfo =
                await _peerConnectionRepository.GetByDirectMessage(networkIdentitySigningKey);

            if (connnectionInfo is null)
            {
                throw new RpcException(new Status(StatusCode.NotFound, "Peer connection info not found."));
            }

            var networkPeerId = new NetworkPeerId(connnectionInfo.Id.Value);
                
            var peer = await _peerRepository.GetByIdAsync(new IdentityPeerId(networkPeerId.Value));
            if (peer is null)
            {
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Could not determine peer from signing key."));
            }
            var peerId = peer.Id;
            var signedPreKeyId = new Guid(payload.SignedPreKey.Id.ToByteArray());
            
            var domainBundles = new List<Percolator.Cryptography.PreKeyBundle>();
            foreach (var oneTimePreKey in payload.OneTimePreKeys)
            {
                if (oneTimePreKey is null || !oneTimePreKey.HasId || !oneTimePreKey.HasPublicKey)
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "Request must contain one-time pre-key."));
                }

                if (!oneTimePreKey.HasId)
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "Request must contain one-time pre-key ID."));
                }
                var oneTimePreKeyId = new Guid(oneTimePreKey.Id.ToByteArray());
                
                var domainBundle = new Percolator.Cryptography.PreKeyBundle(
                    new RatchetIdentityKey(identitySigningKeyBytes),
                    signedPreKeyId,
                    new PreKey(signedPreKeyBytes),  
                    signedPreKeySignature,
                    oneTimePreKeyId,
                    new OneTimeKey( oneTimePreKey.PublicKey.ToByteArray()),
                    payload.ExpiresUtc.ToDateTime()
                );
                
                domainBundles.Add(domainBundle);
            }

            if (domainBundles.Count == 0)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "No valid bundles were provided."));
            }

            await _bundleRepository.StoreBundlesAsync(new Percolator.Cryptography.Primitives.PeerId(peerId.Value), domainBundles);
            _logger.LogInformation("Successfully stored {BundleCount} pre-key bundles for peer {PeerId}", domainBundles.Count, peerId);

            return new SubmitPreKeyBundleResponse { Version = 1 };
        }

        public override async Task<DeliverOpaqueMessageResponse> DeliverOpaqueMessage(DeliverOpaqueMessageRequest request, ServerCallContext context)
        {
            _logger.LogInformation("Received request to deliver an opaque message");
            try
            {
                var sessionId = new SessionId(Guid.Parse(request.SessionId));

                // Create a SessionRatchetMessage from the payload bytes
                var payload = request.Payload.ToByteArray();
                var sessionRatchetMessage = new SessionRatchetMessage(payload);
                
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
                var plaintext = await _sessionManager.ReceiveMessageAsync(sessionId, sessionRatchetMessage);
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
                    case InternalEnvelope.ApplicationPayloadOneofCase.DhtEnvelope:
                        
                        await HandleDhtMessageAsync(internalEnvelope.DhtEnvelope, sessionId);
                        break;
                    default:
                        _logger.LogWarning("Received unhandled internal envelope type: {EnvelopeType}", internalEnvelope.ApplicationPayloadCase);
                        break;
                }


                return new DeliverOpaqueMessageResponse();
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
                    //todo: add a new MessagesService.ReceiveMessageAsync method to the MediatoR handler
                    // Here you would typically publish a MediatR notification
                    // for another service to handle the text message.
                    break;
                // Handle other chat message types
                default:
                    _logger.LogWarning("Received unhandled chat message type: {MessageType}", chatEnvelope.MessageCase);
                    break;
            }
        }

        private async Task HandleDhtMessageAsync(DhtEnvelope dhtEnvelope, SessionId sessionId)
        {
            var remotePeerId = await _sessionManager.GetRemotePeerIdFromDirectMessage(sessionId);
            var connectionInfo = await _peerConnectionRepository.GetByIdAsync(new NetworkPeerId(remotePeerId.Value));
            if (connectionInfo?.GrpcEndPoints.FirstOrDefault() is null)
            {
                _logger.LogWarning("Could not find connection info for peer {PeerId} to handle DHT message", remotePeerId);
                return;
            }
            if (connectionInfo.IdentitySigningKey is null)
            {
                _logger.LogWarning("Could not find identity signing key for peer {PeerId} to handle DHT message", remotePeerId);
                return;
            }
            var endpoint = connectionInfo.GrpcEndPoints.First().EndPoint;

            switch (dhtEnvelope.MessageCase)
            {
                case DhtEnvelope.MessageOneofCase.PingRequest:
                    await _mediator.Send(new Dht.Messages.PingRequest(new Dht.NodeId(connectionInfo.IdentitySigningKey.Value), endpoint));
                    break;
                case DhtEnvelope.MessageOneofCase.None:
                case DhtEnvelope.MessageOneofCase.FindNodeRequest:
                case DhtEnvelope.MessageOneofCase.FindNodeResponse:
                case DhtEnvelope.MessageOneofCase.PingResponse:
                default:
                    _logger.LogWarning("Received unhandled DHT message type: {MessageType}", dhtEnvelope.MessageCase);
                    break;
            }
        }
    }
}