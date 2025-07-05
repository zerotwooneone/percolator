using System.Text.Json;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Percolator.Application.KeyExchange;
using Percolator.Application.Sessions;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Sessions;
using SessionPeerId = Percolator.Sessions.PeerId;
using Google.Protobuf;
using System.Security.Cryptography;
using Percolator.Application.Identity;

namespace Percolator.Application.Network
{
    public class PercolatorMessageService : TransportService.TransportServiceBase
    {
        private readonly ILogger<PercolatorMessageService> _logger;
        private readonly DirectSessionManager _sessionManager;
        private readonly X3DHOrchestrator _x3dhOrchestrator;
        private readonly ActiveIdentityContext _activeIdentityContext;

        public PercolatorMessageService(ILogger<PercolatorMessageService> logger, DirectSessionManager sessionManager, X3DHOrchestrator x3dhOrchestrator, ActiveIdentityContext activeIdentityContext)
        {
            _logger = logger;
            _sessionManager = sessionManager;
            _x3dhOrchestrator = x3dhOrchestrator;
            _activeIdentityContext = activeIdentityContext;
        }

        public override async Task<EstablishSessionResponse> EstablishSession(EstablishSessionRequest request, ServerCallContext context)
        {
            _logger.LogInformation("Received request to establish a new session.");
            try
            {
                // Perform a best-effort check to assign a stable PeerId for our application layer.
                // If a long-term identity key is provided, we use it to derive a stable ID.
                // Otherwise, we derive it from the ephemeral bundle key, meaning the peer will
                // appear as a new identity on each connection if they don't use a long-term key.
                SessionPeerId remotePeerId;
                if (request.HasLongTermIdentityKey)
                {
                    using var sha256 = SHA256.Create();
                    var hash = sha256.ComputeHash(request.LongTermIdentityKey.ToByteArray());
                    var guid = new Guid(hash.AsSpan(0, 16));
                    remotePeerId = new SessionPeerId(guid);
                    _logger.LogInformation("Identified peer {PeerId} using provided long-term identity key.", remotePeerId);
                }
                else
                {
                    using var sha256 = SHA256.Create();
                    var hash = sha256.ComputeHash(request.InitiatorBundle.IdentityAgreementKey.ToByteArray());
                    var guid = new Guid(hash.AsSpan(0, 16));
                    remotePeerId = new SessionPeerId(guid);
                    _logger.LogInformation("No long-term identity key provided. Identified peer {PeerId} using ephemeral bundle key.", remotePeerId);
                }

                // Step 1: Use the orchestrator to process the incoming handshake.
                var orchestratorResult = _x3dhOrchestrator.ProcessHandshake(
                    request.InitiatorBundle,
                    request.InitiatorEphemeralKey.ToByteArray()
                );

                // Step 2: Use the shared secret to establish a new Double Ratchet session.
                var remoteIdentityPublicKey = new OpaquePublicKey(request.InitiatorBundle.IdentityAgreementKey.ToByteArray());
                var conversationId = await _sessionManager.EstablishSessionAsync(
                    remotePeerId,
                    remoteIdentityPublicKey,
                    orchestratorResult.SharedSecret
                );

                _logger.LogInformation("Successfully established session {SessionId} with peer {PeerId}", conversationId, remotePeerId);

                if (_activeIdentityContext.Identity is null)
                {
                    _logger.LogError("Local peer identity has not been established. Cannot respond to handshake.");
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, "Server identity not initialized."));
                }

                return new EstablishSessionResponse
                {
                    SessionId = conversationId.Value.ToString(),
                    ResponderBundle = orchestratorResult.ResponderBundle,
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
                var conversationId = new ConversationId(Guid.Parse(request.SessionId));

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