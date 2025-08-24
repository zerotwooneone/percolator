using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Sessions;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Network;

namespace Percolator.Application.Network
{
    public class DeliverOpaqueMessageHandler : IRequestHandler<DeliverOpaqueMessageCommand, DeliverOpaqueMessageResult>
    {
        private readonly ILogger<DeliverOpaqueMessageHandler> _logger;
        private readonly IDirectSessionManager _sessionManager;
        private readonly IPeerConnectionRepository _peerConnectionRepository;
        private readonly IMediator _mediator;

        public DeliverOpaqueMessageHandler(
            ILogger<DeliverOpaqueMessageHandler> logger,
            IDirectSessionManager sessionManager,
            IPeerConnectionRepository peerConnectionRepository,
            IMediator mediator)
        {
            _logger = logger;
            _sessionManager = sessionManager;
            _peerConnectionRepository = peerConnectionRepository;
            _mediator = mediator;
        }

        public async Task<DeliverOpaqueMessageResult> Handle(DeliverOpaqueMessageCommand request, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Processing opaque message for session {SessionId}", request.SessionId);
            try
            {
                var sessionId = new SessionId(request.SessionId);
                var sessionRatchetMessage = new SessionRatchetMessage(request.PayloadBytes);

                // Optional: version sniffing
                try
                {
                    var protoMessage = Contracts.RatchetMessage.Parser.ParseFrom(request.PayloadBytes);
                    if (protoMessage.Version > 1)
                    {
                        _logger.LogWarning("Received message with newer version {Version} than supported (1)", protoMessage.Version);
                    }
                }
                catch (Google.Protobuf.InvalidProtocolBufferException ex)
                {
                    _logger.LogDebug(ex, "Payload not parseable as RatchetMessage; may be legacy frame");
                }

                var plaintext = await _sessionManager.ReceiveMessageAsync(sessionId, sessionRatchetMessage);
                if (plaintext is null)
                {
                    _logger.LogWarning("Decryption resulted in null plaintext for session {SessionId}. This may be a skipped message.", request.SessionId);
                    return new DeliverOpaqueMessageResult();
                }

                var internalEnvelope = InternalEnvelope.Parser.ParseFrom(plaintext.Value);
                InternalEnvelope? responseEnvelope = null;

                switch (internalEnvelope.ApplicationPayloadCase)
                {
                    case InternalEnvelope.ApplicationPayloadOneofCase.ChatEnvelope:
                        HandleChatEnvelope(internalEnvelope.ChatEnvelope);
                        break;
                    case InternalEnvelope.ApplicationPayloadOneofCase.DhtEnvelope:
                        responseEnvelope = await HandleDhtMessageAsync(internalEnvelope.DhtEnvelope, sessionId, cancellationToken);
                        break;
                    default:
                        _logger.LogWarning("Received unhandled internal envelope type: {EnvelopeType}", internalEnvelope.ApplicationPayloadCase);
                        break;
                }

                if (responseEnvelope is not null)
                {
                    var responseBytes = await EncryptResponseEnvelope(sessionId, responseEnvelope);
                    return new DeliverOpaqueMessageResult { ResponsePayloadBytes = responseBytes };
                }

                return new DeliverOpaqueMessageResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing opaque message for session {SessionId}", request.SessionId);
                throw;
            }
        }

        private void HandleChatEnvelope(ChatEnvelope chatEnvelope)
        {
            switch (chatEnvelope.MessageCase)
            {
                case ChatEnvelope.MessageOneofCase.TextMessage:
                    _logger.LogInformation("Received Text Message: {Content}", chatEnvelope.TextMessage.Content);
                    break;
                default:
                    _logger.LogWarning("Received unhandled chat message type: {MessageType}", chatEnvelope.MessageCase);
                    break;
            }
        }

        private async Task<InternalEnvelope?> HandleDhtMessageAsync(DhtEnvelope dhtEnvelope, SessionId sessionId, CancellationToken ct)
        {
            var remotePeerId = await _sessionManager.GetRemotePeerIdFromDirectMessage(sessionId);
            var connectionInfo = await _peerConnectionRepository.GetByIdAsync(new Percolator.Network.PeerId(remotePeerId.Value));
            if (connectionInfo?.GrpcEndPoints.FirstOrDefault() is null)
            {
                _logger.LogWarning("Could not find connection info for peer {PeerId} to handle DHT message", remotePeerId);
                return null;
            }
            if (connectionInfo.IdentitySigningKey is null)
            {
                _logger.LogWarning("Could not find identity signing key for peer {PeerId} to handle DHT message", remotePeerId);
                return null;
            }

            var endpoint = connectionInfo.GrpcEndPoints.First().EndPoint;

            switch (dhtEnvelope.MessageCase)
            {
                //todo: avoid sending mediatR requests from mediatR handlers
                case DhtEnvelope.MessageOneofCase.PingRequest:
                    // NodeId is defined as SHA-256 digest of the peer's SPKI signing key bytes (32 bytes)
                    var nodeIdBytes = System.Security.Cryptography.SHA256.HashData(connectionInfo.IdentitySigningKey.Value);
                    await _mediator.Send(new Percolator.Dht.Messages.PingRequest(new Percolator.Dht.NodeId(nodeIdBytes), endpoint), ct);
                    break;
                case DhtEnvelope.MessageOneofCase.FindNodeRequest:
                    var findNodeResponse = await _mediator.Send(new Percolator.Dht.Messages.FindNodeRequest(new Percolator.Dht.NodeId(dhtEnvelope.FindNodeRequest.TargetPeerId.ToByteArray())), ct);
                    var responseEnvelope = new InternalEnvelope
                    {
                        DhtEnvelope = new DhtEnvelope
                        {
                            FindNodeResponse = new Contracts.FindNodeResponse()
                        }
                    };
                    responseEnvelope.DhtEnvelope.FindNodeResponse.CloserPeers.AddRange(findNodeResponse.CloserNodes.Select(n =>
                        new NodeInfo
                        {
                            PeerId = ByteString.CopyFrom(n.Id.Value),
                            Address = n.EndPoint.ToString()
                        }));

                    return responseEnvelope;
                default:
                    _logger.LogWarning("Received unhandled DHT message type: {MessageType}", dhtEnvelope.MessageCase);
                    break;
            }

            return null;
        }

        private async Task<byte[]> EncryptResponseEnvelope(SessionId sessionId, InternalEnvelope internalEnvelope)
        {
            var plaintext = new Plaintext(internalEnvelope.ToByteArray());
            var (_, ratchetMessage) = await _sessionManager.EncryptMessageAsync(sessionId, plaintext);
            return ratchetMessage.Value;
        }
    }
}
