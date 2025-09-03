using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Sessions;
using Percolator.Application.Identity;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Network;
using Percolator.Prekey.Handlers;

namespace Percolator.Application.Network
{
    public class DeliverOpaqueMessageHandler : IRequestHandler<DeliverOpaqueMessageCommand, DeliverOpaqueMessageResult>
    {
        private readonly ILogger<DeliverOpaqueMessageHandler> _logger;
        private readonly IDirectSessionManager _sessionManager;
        private readonly IPeerConnectionRepository _peerConnectionRepository;
        private readonly IMediator _mediator;
        private readonly IDirectSessionRepository _directSessionRepository;
        private readonly ActiveIdentityContext _activeIdentityContext;

        public DeliverOpaqueMessageHandler(
            ILogger<DeliverOpaqueMessageHandler> logger,
            IDirectSessionManager sessionManager,
            IPeerConnectionRepository peerConnectionRepository,
            IMediator mediator,
            IDirectSessionRepository directSessionRepository,
            ActiveIdentityContext activeIdentityContext)
        {
            _logger = logger;
            _sessionManager = sessionManager;
            _peerConnectionRepository = peerConnectionRepository;
            _mediator = mediator;
            _directSessionRepository = directSessionRepository;
            _activeIdentityContext = activeIdentityContext;
        }

        private async Task HandlePrekeyEnvelopeAsync(PrekeyEnvelope prekeyEnvelope, CancellationToken ct)
        {
            switch (prekeyEnvelope.MessageCase)
            {
                case PrekeyEnvelope.MessageOneofCase.SubmitPreKeyBundleRequest:
                    var upload = prekeyEnvelope.SubmitPreKeyBundleRequest;
                    if (!upload.HasIdentityKey)
                    {
                        throw new InvalidOperationException("Identity key is required");
                    }
                    if (!upload.HasSignedPreKeyId)
                    {
                        throw new InvalidOperationException("Signed pre-key ID is required");
                    }
                    if (!upload.HasSignedPreKey)
                    {
                        throw new InvalidOperationException("Signed pre-key is required");
                    }
                    if (!upload.HasPreKeySignature)
                    {
                        throw new InvalidOperationException("Pre-key signature is required");
                    }

                    if (upload.OneTimePreKeys.Count == 0)
                    {
                        throw new InvalidOperationException("At least one one-time pre-key is required");
                    }
                    const int maxBundles = 100;
                    if(upload.OneTimePreKeys.Count > maxBundles)
                    {
                        throw new InvalidOperationException($"Too many one-time pre-keys. Maximum is {maxBundles}");
                    }
                    foreach (var oneTimePreKey in upload.OneTimePreKeys)
                    {
                        if (!oneTimePreKey.HasId)
                        {
                            throw new InvalidOperationException("One-time pre-key is required");
                        }

                        if (!oneTimePreKey.HasPublicKey)
                        {
                            throw new InvalidOperationException("One-time pre-key public key is required");
                        }
                    }
                    if (upload.ExpiresUtc.ToDateTimeOffset() < DateTimeOffset.Now)
                    {
                        throw new InvalidOperationException("Pre-key bundle has expired");
                    }
                    var cmd = new SubmitPreKeyBundleCommand
                    {
                        PublicSigningKey = upload.IdentityKey.ToByteArray(),
                        SignedPreKeyId = new Guid(upload.SignedPreKeyId.ToByteArray()),
                        SignedPreKey = upload.SignedPreKey.ToByteArray(),
                        PreKeySignature = upload.PreKeySignature.ToByteArray(),
                        OneTimePreKeys = upload.OneTimePreKeys.Select(x => new SubmitPreKeyBundleCommand.OneTimePreKey(
                            new Guid(x.Id.ToByteArray()), x.PublicKey.ToByteArray())).ToList(),
                        Expires = upload.ExpiresUtc.ToDateTimeOffset()
                    };
                    await _mediator.Send(cmd, ct);
                    break;
                default:
                    _logger.LogWarning("Received unhandled prekey message type: {MessageType}", prekeyEnvelope.MessageCase);
                    break;
            }
        }

        public async Task<DeliverOpaqueMessageResult> Handle(DeliverOpaqueMessageCommand request, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Processing opaque message for session {SessionId}", request.SessionId);
            try
            {
                var sessionId = new SessionId(request.SessionId);
                var directSession = await _directSessionRepository.GetBySessionIdAsync(new DirectSessionId(sessionId.Value), _activeIdentityContext.Identity.SelfIdentityId);
                if (directSession is null)
                {
                    throw new InvalidOperationException($"No direct session mapping found for session {sessionId}");
                }
                var remotePeerId = directSession.RemotePeerId;
                _logger.LogInformation("Resolved remote peer {PeerId} for session {SessionId}", remotePeerId, directSession.SessionId);
                var connectionInfo = await _peerConnectionRepository.GetByIdAsync(remotePeerId);
                if (connectionInfo?.GrpcEndPoints.FirstOrDefault() is null)
                {
                    _logger.LogWarning("Could not find connection info for peer {PeerId} to handle opaque message", remotePeerId);
                    return new DeliverOpaqueMessageResult();
                }
                
                //todo: find a better way to find the current endpoint
                var endpoint = connectionInfo.GrpcEndPoints.First();
                _logger.LogInformation("Using endpoint {Endpoint} for peer {PeerId}", endpoint, remotePeerId);
                
                var sessionRatchetMessage = new SessionRatchetMessage(request.PayloadBytes);

                var plaintext = await _sessionManager.ReceiveMessageAsync(sessionId, sessionRatchetMessage);
                if (plaintext is null)
                {
                    _logger.LogWarning("Decryption resulted in null plaintext for session {SessionId}. This may be a skipped message.", request.SessionId);
                    return new DeliverOpaqueMessageResult();
                }

                var internalEnvelope = InternalEnvelope.Parser.ParseFrom(plaintext.Value);
                InternalEnvelope? responseEnvelope = null;
                
                connectionInfo.UpdateLastSeen(endpoint, DateTimeOffset.UtcNow);
                await _peerConnectionRepository.SaveAsync(connectionInfo);

                switch (internalEnvelope.ApplicationPayloadCase)
                {
                    case InternalEnvelope.ApplicationPayloadOneofCase.ChatEnvelope:
                        HandleChatEnvelope(internalEnvelope.ChatEnvelope);
                        break;
                    case InternalEnvelope.ApplicationPayloadOneofCase.DhtEnvelope:
                        responseEnvelope = await HandleDhtMessageAsync(internalEnvelope.DhtEnvelope, connectionInfo, endpoint, cancellationToken);
                        break;
                    case InternalEnvelope.ApplicationPayloadOneofCase.PrekeyEnvelope:
                        await HandlePrekeyEnvelopeAsync(internalEnvelope.PrekeyEnvelope, cancellationToken);
                        break;
                    default:
                        _logger.LogWarning("Received unhandled internal envelope type: {EnvelopeType}", internalEnvelope.ApplicationPayloadCase);
                        break;
                }

                if (responseEnvelope is null)
                {
                    return new DeliverOpaqueMessageResult();
                }

                var responseBytes = await EncryptResponseEnvelope(sessionId, responseEnvelope);
                return new DeliverOpaqueMessageResult { ResponsePayloadBytes = responseBytes };

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

        private async Task<InternalEnvelope?> HandleDhtMessageAsync(DhtEnvelope dhtEnvelope, PeerConnection peerConnection, GrpcEndPoint endPoint, CancellationToken ct)
        {
            if (_activeIdentityContext.Identity is null)
            {
                _logger.LogError("No active identity available");
                return null;
            }
            // Resolve the remote peer from the direct session mapping

            switch (dhtEnvelope.MessageCase)
            {
                //todo: avoid sending mediatR requests from mediatR handlers
                case DhtEnvelope.MessageOneofCase.PingRequest:
                    if (peerConnection.IdentitySigningKey is null)
                    {
                        _logger.LogWarning("Could not find identity signing key for peer {PeerId} to handle DHT message", peerConnection.Id);
                        return null;
                    }
                    // NodeId is defined as SHA-256 digest of the peer's SPKI signing key bytes (32 bytes)
                    var nodeIdBytes = System.Security.Cryptography.SHA256.HashData(peerConnection.IdentitySigningKey.Value);
                    await _mediator.Send(new Percolator.Dht.Messages.PingRequest(new Percolator.Dht.NodeId(nodeIdBytes), endPoint.EndPoint), ct);
                    break;
                case DhtEnvelope.MessageOneofCase.FindNodeRequest:
                    //todo: prevent finding nodes if the peer has not given us prekey bundles
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
            var ratchetMessage = await _sessionManager.EncryptMessageAsync(sessionId, plaintext);
            return ratchetMessage.Value;
        }
    }
}
