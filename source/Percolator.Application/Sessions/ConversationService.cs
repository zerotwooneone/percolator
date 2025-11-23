using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Percolator.Application.Configuration;
using Percolator.Application.Network;
using Percolator.Chat;
using Percolator.Contracts;
using Percolator.Identity;
using IdentityPeer = Percolator.Identity.Peer;
using IdentityPeerId = Percolator.Identity.PeerId;
using Percolator.Network;
using System.Net;
using Google.Protobuf;
using Percolator.Application.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Percolator.Application.KeyExchange;
using Percolator.Application.Services;
using Percolator.Chat.ValueObjects;
using Percolator.Cryptography;
using CryptoSignature = Percolator.Cryptography.Signature;
using ChatConversation = Percolator.Chat.Conversation;
using ChatConversationId = Percolator.Chat.ValueObjects.ConversationId;
using ChatParticipantId = Percolator.Chat.ValueObjects.ParticipantId;
using NetworkPeerId = Percolator.Network.PeerId;
using ContractsPreKeyBundle = Percolator.Contracts.PreKeyBundle;

namespace Percolator.Application.Sessions
{
    internal class ConversationService : IConversationService
    {
        private readonly ILogger<ConversationService> _logger;
        private readonly IOneTimeKeyProvider _oneTimeKeyProvider;
        private readonly ActiveIdentityContext _activeIdentityContext;
        private readonly IGrpcSessionService _grpcSessionService;
        private readonly IPeerRoutingProfileRepository _profileRepository;
        private readonly IOptions<TransportOptions> _transportOptions;
        private readonly IDirectSessionRepository _directSessionRepository;
        private readonly IPeerPublicSigningKeyStore _pkhStore;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IOptions<CryptographyOptions> _cryptographyOptions;
        private readonly IHandshakeService _handshake;
        private readonly ISecureMessagingService _secureMessaging;

        public ConversationService(
            ILogger<ConversationService> logger,
            IOneTimeKeyProvider oneTimeKeyProvider,
            ActiveIdentityContext activeIdentityContext,
            IGrpcSessionService grpcSessionService, 
            IPeerRoutingProfileRepository profileRepository,
            IOptions<TransportOptions> transportOptions,
            IDirectSessionRepository directSessionRepository,
            IPeerPublicSigningKeyStore pkhStore,
            ILoggerFactory loggerFactory,
            IOptions<CryptographyOptions> cryptographyOptions,
            IHandshakeService handshake,
            ISecureMessagingService secureMessaging)
        {
            _logger = logger;
            _oneTimeKeyProvider = oneTimeKeyProvider;
            _activeIdentityContext = activeIdentityContext;
            _grpcSessionService = grpcSessionService;
            _profileRepository = profileRepository;
            _transportOptions = transportOptions;
            _directSessionRepository = directSessionRepository;
            _pkhStore = pkhStore;
            _loggerFactory = loggerFactory;
            _cryptographyOptions = cryptographyOptions;
            _handshake = handshake;
            _secureMessaging = secureMessaging;
        }

        public async Task<DirectSessionId?> GetExistingDirectSessionAsync(
            Peer remotePeer)
        {
            if (_activeIdentityContext.Identity is null)
            {
                _logger.LogError("No active identity available");
                return null;
            }

            // IDirectSessionRepository is the source of truth for mapping a remote peer to a direct session
            var mapping = await _directSessionRepository.GetByRemotePeerIdAsync(
                new NetworkPeerId(remotePeer.Id.Value),
                _activeIdentityContext.Identity.SelfIdentityId).ConfigureAwait(false);

            if (mapping is null)
            {
                _logger.LogInformation("No existing direct session mapping found for peer {PeerName} ({PeerId})",
                    remotePeer.Name, remotePeer.Id.Value);
                return null;
            }

            _logger.LogInformation("Existing direct session with {PeerName} found. Reusing session {SessionId}",
                remotePeer.Name, mapping.SessionId.Value);
            return mapping.SessionId;
        }

        public async Task<DirectSessionId> CreateNewDirectSessionAsync(
            DnsEndPoint endpoint,
            Peer remotePeer)
        {
            
            try
            {
                _logger.LogInformation("Creating direct conversation with {PeerName} at {Endpoint}", remotePeer.Name,
                    endpoint);

                // Verify identity and keys are available
                if (_activeIdentityContext.Identity == null || _activeIdentityContext.Keys == null)
                {
                    _logger.LogError("No active identity or keys available");
                    throw new InvalidOperationException("No active identity or keys available");
                }

                var signedPreKeyPublicBytes =
                    _activeIdentityContext.Keys.SignedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
                var directPayload = new EstablishDirectSessionRequest.Types.DirectInitiatorPayload
                {
                    CallbackPort = (uint)_transportOptions.Value.GrpcPort,
                    ResponderEphemeralKey = ByteString.CopyFrom(signedPreKeyPublicBytes)
                };

                var signedPayload = directPayload.ToByteString();
                // Prepare handshake request
                // TODO: Replace with Domain-aligned signing via SessionCrypto adapter in Step 8
                throw new NotSupportedException("Direct session handshake cutover pending (Step 8): replace legacy SignPreKey");

                var oneTimePreKey = _oneTimeKeyProvider.PopOneTimeKey();
                var request = new EstablishDirectSessionRequest
                {
                    ResponderBundle = new ContractsPreKeyBundle
                    {
                        SignedPayload = signedPayload,
                        IdentitySigningKey = Google.Protobuf.ByteString.CopyFrom(_activeIdentityContext.Keys
                            .IdentitySigningKey.ExportSubjectPublicKeyInfo()),
                        OneTimePreKey = oneTimePreKey is null
                            ? ByteString.Empty
                            : Google.Protobuf.ByteString.CopyFrom(oneTimePreKey.PublicKey.ExportSubjectPublicKeyInfo()),
                        PayloadSignature = ByteString.Empty
                    }
                };

                _logger.LogInformation("Sending session request to {Endpoint}", endpoint);
                var message = await _grpcSessionService.EstablishDirectSessionAsync(endpoint, request).ConfigureAwait(false);
                if (message.MessageCase != EstablishDirectSessionResponse.MessageOneofCase.Response)
                {
                    //todo: handle not until
                    throw new InvalidOperationException($"Invalid response type:{message.MessageCase}");
                }
                var response = message.Response!;
                _logger.LogInformation("Received session response from peer");
                
                var firstMessage = new SessionRatchetMessage(response.RatchetMessage.ToByteArray());
                // TODO: Complete responder-side handshake via IHandshakeService and persist session
                throw new NotSupportedException("Direct session handshake cutover pending (Step 8): replace legacy CompleteHandshake");
                
                // Unreachable in interim NotSupported flow
                // Determine or allocate a DirectSessionId mapping for this peer (cutover pending Step 8)
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to establish direct conversation with {PeerName}", remotePeer.Name);
                throw;
            }
        }
    }
}