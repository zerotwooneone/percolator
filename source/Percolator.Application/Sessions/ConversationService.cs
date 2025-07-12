using System.Security.Cryptography;
using System.Formats.Asn1;
using System.Net;
using Google.Protobuf;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Application.Network;
using Percolator.Chat;
using Percolator.Chat.ValueObjects;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;
using Percolator.Sessions;
using ChatConversation = Percolator.Chat.Conversation;
using ChatConversationId = Percolator.Chat.ValueObjects.ConversationId;
using ChatParticipantId = Percolator.Chat.ValueObjects.ParticipantId;
using IdentityPeerId = Percolator.Identity.PeerId;
using NetworkPeerId = Percolator.Network.PeerId;
using SessionPeerId = Percolator.Sessions.PeerId;
using SessionConversationId = Percolator.Sessions.ConversationId;
using ContractsPreKeyBundle = Percolator.Contracts.PreKeyBundle;

namespace Percolator.Application.Sessions;

public class ConversationService : IConversationService
{
    private readonly ActiveIdentityContext _activeIdentityContext;
    private readonly X3DHOrchestrator _orchestrator;
    private readonly DirectSessionManager _sessionManager;
    private readonly IConversationRepository _conversationRepository;
    private readonly IPeerRepository _peerRepository;
    private readonly IPeerConnectionRepository _peerConnectionRepository;
    private readonly ILocalPeerProvider _localPeerProvider;
    private readonly IGrpcClientFactory _grpcClientFactory;
    private readonly ILogger<ConversationService> _logger;

    public ConversationService(
        ActiveIdentityContext activeIdentityContext,
        X3DHOrchestrator orchestrator,
        DirectSessionManager sessionManager,
        IConversationRepository conversationRepository,
        IPeerRepository peerRepository,
        IPeerConnectionRepository peerConnectionRepository,
        ILocalPeerProvider localPeerProvider,
        IGrpcClientFactory grpcClientFactory,
        ILogger<ConversationService> logger)
    {
        _activeIdentityContext = activeIdentityContext;
        _orchestrator = orchestrator;
        _sessionManager = sessionManager;
        _conversationRepository = conversationRepository;
        _peerRepository = peerRepository;
        _peerConnectionRepository = peerConnectionRepository;
        _localPeerProvider = localPeerProvider;
        _grpcClientFactory = grpcClientFactory;
        _logger = logger;
    }

    public async Task<ChatConversationId> CreateDirectConversationAsync(DnsEndPoint endpoint, string peerName, TlsCertificate? tlsCertificate = null)
    {
        _logger.LogInformation("Attempting to create direct conversation with {endpoint}", endpoint);

        var localKeys = _activeIdentityContext.Keys;
        if (localKeys is null)
        {
            throw new InvalidOperationException("Could not find local identity. Please create one first.");
        }

        if (localKeys.OneTimePreKeys is null || localKeys.OneTimePreKeys.Length == 0)
        {
            throw new InvalidOperationException("Could not find any one-time pre-keys for the local identity.");
        }

        using var signedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var signedPreKeyPublicBytes = signedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
        var signature = localKeys.IdentitySigningKey.SignData(signedPreKey.PublicKey.ExportSubjectPublicKeyInfo(), HashAlgorithmName.SHA256);

        var localBundle = new ContractsPreKeyBundle
        {
            IdentitySigningKey = ByteString.CopyFrom(localKeys.IdentitySigningKey.ExportSubjectPublicKeyInfo()),
            IdentityAgreementKey = ByteString.CopyFrom(localKeys.IdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo()),
            SignedPreKey = ByteString.CopyFrom(signedPreKeyPublicBytes),
            PreKeySignature = ByteString.CopyFrom(signature)
        };

        using var ephemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        var localPeerId = await _localPeerProvider.GetPeerIdAsync();

        var request = new EstablishSessionRequest
        {
            InitiatorBundle = localBundle,
            InitiatorEphemeralKey = ByteString.CopyFrom(ephemeralKey.PublicKey.ExportSubjectPublicKeyInfo())
        };

        var client = _grpcClientFactory.CreateClient(endpoint, tlsCertificate);

        _logger.LogInformation("Sending EstablishSessionRequest to {endpoint}", endpoint);
        var response = await client.EstablishSessionAsync(request);

        Peer? peer = await _peerRepository.GetByNameAsync(peerName);

        var remotePeerIdentityKey = response.ResponderBundle.IdentitySigningKey.ToByteArray();
        // If peer is unknown, this is a TOFU scenario.
        if (peer is null)
        {
            if (tlsCertificate is not null)
            {
                // This case should ideally not be hit if the CLI enforces providing a name for a known peer cert
                throw new InvalidOperationException("A certificate was provided, but the peer is unknown. Please add the peer first.");
            }

            _logger.LogInformation("Peer '{PeerName}' not found. Creating new peer from TOFU handshake.", peerName);

            var tlsCertificateFromHandshake = new TlsCertificate(remotePeerIdentityKey);
            var newPeer = new Peer(new IdentityPeerId(Guid.NewGuid()), peerName);
            await _peerRepository.AddAsync(newPeer);
            peer = newPeer;

            var peerConnection = new PeerConnection(
                new NetworkPeerId(peer.Id.Value),
                null, // DirectMessagePublicKey is not available at this stage
                new[] { new GrpcEndPoint(endpoint, DateTimeOffset.UtcNow) },
                new[] { tlsCertificateFromHandshake },
                DateTimeOffset.UtcNow);

            await _peerConnectionRepository.SaveAsync(peerConnection);
        }

        var remotePeerId = new SessionPeerId(peer.Id.Value);
        var sharedSecret = _orchestrator.CompleteHandshake(response.ResponderBundle, ephemeralKey);

        var channelId = new ChannelId(remotePeerIdentityKey);
        var conversation = (await _conversationRepository.GetByChannelIdAsync(channelId))
                           ?? new ChatConversation(
                               new ChatConversationId(Guid.NewGuid()),
                               channelId,
                               new List<ChatParticipantId>
                               {
                                   new(localPeerId.Value),
                                   new(remotePeerId.Value)
                               },
                               new List<Message>(),
                               peerName);

        await _conversationRepository.AddAsync(conversation);

        await _sessionManager.EstablishSessionAsInitiatorAsync(
            new SessionConversationId(conversation.Id.Value),
            remotePeerId,
            new SessionIdentityKey(response.ResponderBundle.IdentityAgreementKey.ToByteArray()),
            new SessionRatchetKey(response.ResponderBundle.SignedPreKey.ToByteArray()),
            sharedSecret);

        _logger.LogInformation("Successfully established session and created conversation {ConversationId}", conversation.Id.Value);

        return conversation.Id;
    }

    public Task<ChatConversationId?> GetLastActiveConversationIdAsync(Guid peerId)
    {
        //todo: create Session domain peer that tracks last active conversation
        return Task.FromResult<ChatConversationId?>(null);
    }
}