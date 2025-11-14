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
        private readonly IX3DHOrchestrator _orchestrator;
        private readonly IDirectSessionManager _sessionManager;
        private readonly IOneTimeKeyProvider _oneTimeKeyProvider;
        private readonly ActiveIdentityContext _activeIdentityContext;
        private readonly IGrpcSessionService _grpcSessionService;
        private readonly IX3DHManager _x3DhManager;
        private readonly IPeerRoutingProfileRepository _profileRepository;
        private readonly IOptions<TransportOptions> _transportOptions;
        private readonly IDirectSessionRepository _directSessionRepository;
        private readonly IPeerPublicSigningKeyStore _pkhStore;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IOptions<CryptographyOptions> _cryptographyOptions;

        public ConversationService(
            ILogger<ConversationService> logger,
            IX3DHOrchestrator orchestrator,
            IDirectSessionManager sessionManager,
            IOneTimeKeyProvider oneTimeKeyProvider,
            ActiveIdentityContext activeIdentityContext,
            IGrpcSessionService grpcSessionService, 
            IX3DHManager x3DhManager, 
            IPeerRoutingProfileRepository profileRepository,
            IOptions<TransportOptions> transportOptions,
            IDirectSessionRepository directSessionRepository,
            IPeerPublicSigningKeyStore pkhStore,
            ILoggerFactory loggerFactory,
            IOptions<CryptographyOptions> cryptographyOptions)
        {
            _logger = logger;
            _orchestrator = orchestrator;
            _sessionManager = sessionManager;
            _oneTimeKeyProvider = oneTimeKeyProvider;
            _activeIdentityContext = activeIdentityContext;
            _grpcSessionService = grpcSessionService;
            _x3DhManager = x3DhManager;
            _profileRepository = profileRepository;
            _transportOptions = transportOptions;
            _directSessionRepository = directSessionRepository;
            _pkhStore = pkhStore;
            _loggerFactory = loggerFactory;
            _cryptographyOptions = cryptographyOptions;
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
                var signedPayloadBytes = _x3DhManager.SignPreKey(_activeIdentityContext.Keys.IdentitySigningKey,
                    new PreKey(signedPayload.ToByteArray()));

                // Log the key formats being used
                _logger.LogDebug(
                    "Initiating handshake with keys - SignedPreKey length: {Length}, Signature length: {SigLength}",
                    signedPreKeyPublicBytes.Length, signedPayloadBytes.Value.Length);
                
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
                        PayloadSignature = ByteString.CopyFrom(signedPayloadBytes.Value)
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

                var initiatorEphemeralKey = response.InitiatorEphemeralKey.ToByteArray();
                var handshakeResult = _orchestrator.CompleteHandshake(
                    new RatchetIdentityKey(response.InitiatorIdentityKey.ToByteArray()),
                    new RatchetEphemeralKey(initiatorEphemeralKey),
                    oneTimePreKey
                );
                
                _logger.LogInformation("Handshake completed locally as Responder");
                
                // Upsert routing profile details even when the peer already existed
                var netPeerId = new NetworkPeerId(remotePeer.Id.Value);
                var profile = await _profileRepository.GetByIdAsync(netPeerId).ConfigureAwait(false)
                    ?? new PeerRoutingProfile();
                var now = DateTimeOffset.UtcNow;
                if (profile.Id is null)
                {
                    profile.BindIdentity(netPeerId);
                }
                // Ensure identity key and endpoint are populated
                profile.SetIdentityPublicKey(new Percolator.Network.ValueObjects.IdentityPublicKey(handshakeResult.ResponderBundle.IdentitySigningKey.Value));
                var existingEndpoint = profile.Endpoints.FirstOrDefault(e => e.EndPoint.Host.Equals(endpoint.Host, StringComparison.OrdinalIgnoreCase) && e.EndPoint.Port == endpoint.Port);
                if (existingEndpoint is null)
                {
                    profile.AddGrpcEndPoint(new GrpcEndPoint(endpoint, now), now);
                }
                else
                {
                    profile.UpdateLastSeen(existingEndpoint, now);
                }
                await _profileRepository.UpsertAsync(profile).ConfigureAwait(false);

                SessionId GetSessionId(Plaintext pt)
                {
                    var responderPayload = EstablishDirectSessionResponse.Types.ResponsePayload.Parser.ParseFrom(pt.Value);
                    var directSessionId = new DirectSessionId(Guid.Parse(responderPayload.SessionId));
                    return new SessionId(directSessionId.Value);
                }
                
                var (cryptoSessionId, plaintext) = await _sessionManager.EstablishSessionAsResponderAsync(
                    firstMessage,
                    GetSessionId,
                    new RatchetIdentityKey(response.InitiatorIdentityKey.ToByteArray()),
                    new RatchetEphemeralKey(response.InitiatorEphemeralKey.ToByteArray()),
                    handshakeResult.ResponderPrivateKeyUsed,
                    new SharedSecret(handshakeResult.SharedSecret.Value)
                ).ConfigureAwait(false);
                
                var directSessionId = new DirectSessionId(cryptoSessionId.Value);
                // Persist mapping from conversation/session to remote peer for future routing
                await _directSessionRepository.UpsertAsync(new NetworkPeerId(remotePeer.Id.Value), directSessionId, _activeIdentityContext.Identity.SelfIdentityId).ConfigureAwait(false);

                // Upsert PKH -> Peer mapping now that the session is established (responder side)
                var spki = handshakeResult.ResponderBundle.IdentitySigningKey.Value;
                var pkh = SHA256.HashData(spki);
                await _pkhStore.ActivateIfChangedAsync(new IdentityPeerId(remotePeer.Id.Value), spki, pkh, DateTimeOffset.UtcNow).ConfigureAwait(false);

                _logger.LogInformation("Successfully established session and created session {DirectSessionId}",
                    directSessionId);

                return directSessionId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to establish direct conversation with {PeerName}", remotePeer.Name);
                throw;
            }
        }
    }
}