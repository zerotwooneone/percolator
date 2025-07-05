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
using OpaquePublicKey = Percolator.Sessions.OpaquePublicKey;

namespace Percolator.Application.Network
{
    public class PercolatorMessageService : TransportService.TransportServiceBase
    {
        private readonly ILogger<PercolatorMessageService> _logger;
        private readonly ActiveIdentityContext _activeIdentityContext;
        private readonly X3DHOrchestrator _x3dhOrchestrator;
        private readonly DirectSessionManager _sessionManager;
        private readonly IConversationRepository _conversationRepository;

        public PercolatorMessageService(ILogger<PercolatorMessageService> logger, ActiveIdentityContext activeIdentityContext, X3DHOrchestrator x3dhOrchestrator, DirectSessionManager sessionManager, IConversationRepository conversationRepository)
        {
            _logger = logger;
            _activeIdentityContext = activeIdentityContext;
            _x3dhOrchestrator = x3dhOrchestrator;
            _sessionManager = sessionManager;
            _conversationRepository = conversationRepository;
        }

        public override async Task<EstablishSessionResponse> EstablishSession(EstablishSessionRequest request, ServerCallContext context)
        {
            _logger.LogInformation("Received request to establish a new session.");
            try
            {
                if (_activeIdentityContext.Identity is null)
                {
                    _logger.LogError("Local peer identity has not been established. Cannot respond to handshake.");
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, "Server identity not initialized."));
                }

                var handshakeResult = _x3dhOrchestrator.ProcessHandshake(request.InitiatorBundle, request.InitiatorEphemeralKey.ToByteArray());

                var initiatorPeerId = new SessionPeerId(new Guid(request.InitiatorPeerId));
                var localPeerId = new SessionPeerId(_activeIdentityContext.Identity.Id);

                var conversation = new ChatConversation(
                    new ChatConversationId(Guid.NewGuid()),
                    new List<ChatParticipantId>
                    {
                        new(localPeerId.Value),
                        new(initiatorPeerId.Value)
                    });

                await _conversationRepository.AddAsync(conversation);

                await _sessionManager.EstablishSessionAsync(
                    new SessionConversationId(conversation.Id.Value),
                    initiatorPeerId,
                    new OpaquePublicKey(request.InitiatorBundle.IdentityAgreementKey.ToByteArray()),
                    handshakeResult.SharedSecret);

                _logger.LogInformation("Successfully established session {SessionId} with peer {PeerId}", conversation.Id, initiatorPeerId);

                return new EstablishSessionResponse
                {
                    SessionId = conversation.Id.Value.ToString(),
                    ResponderBundle = handshakeResult.ResponderBundle,
                    ResponderPeerId = _activeIdentityContext.Identity.Id.ToString()
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
                var ratchetMessage = JsonSerializer.Deserialize<RatchetMessage>(request.Payload.ToByteArray());
                if (ratchetMessage is null)
                {
                    throw new InvalidOperationException("Failed to deserialize RatchetMessage.");
                }

                // Decrypt the message to get the Protobuf-serialized InternalEnvelope
                var internalEnvelopeBytes = await _sessionManager.ReceiveMessageAsync(conversationId, ratchetMessage);

                // Deserialize the InternalEnvelope
                var internalEnvelope = InternalEnvelope.Parser.ParseFrom(internalEnvelopeBytes);

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