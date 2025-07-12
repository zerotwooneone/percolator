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
    private readonly IX3DHOrchestrator _orchestrator;
    private readonly IDirectSessionManager _sessionManager;
    private readonly IConversationRepository _conversationRepository;
    private readonly IPeerRepository _peerRepository;
    private readonly IPeerConnectionRepository _peerConnectionRepository;
    private readonly IGrpcClientFactory _grpcClientFactory;
    private readonly ITlsCertificateService _tlsCertificateService;
    private readonly IOneTimeKeyProvider _oneTimeKeyProvider;
    private readonly ILogger<ConversationService> _logger;

    public ConversationService(
        ActiveIdentityContext activeIdentityContext,
        IX3DHOrchestrator orchestrator,
        IDirectSessionManager sessionManager,
        IConversationRepository conversationRepository,
        IPeerRepository peerRepository,
        IPeerConnectionRepository peerConnectionRepository,
        IGrpcClientFactory grpcClientFactory,
        ITlsCertificateService tlsCertificateService,
        IOneTimeKeyProvider oneTimeKeyProvider,
        ILogger<ConversationService> logger)
    {
        _activeIdentityContext = activeIdentityContext;
        _orchestrator = orchestrator;
        _sessionManager = sessionManager;
        _conversationRepository = conversationRepository;
        _peerRepository = peerRepository;
        _peerConnectionRepository = peerConnectionRepository;
        _grpcClientFactory = grpcClientFactory;
        _tlsCertificateService = tlsCertificateService;
        _oneTimeKeyProvider = oneTimeKeyProvider;
        _logger = logger;
    }

    public async Task<ChatConversationId> CreateDirectConversationAsync(DnsEndPoint endpoint, string peerName)
    {
        _logger.LogInformation("Attempting to create direct conversation with {endpoint}", endpoint);

        var localIdentity = _activeIdentityContext.Identity;
        if (localIdentity is null)
        {
            throw new InvalidOperationException("Could not find local identity. Please create one first.");
        }
        var localKeys = _activeIdentityContext.Keys;
        if (localKeys is null)
        {
            throw new InvalidOperationException("Could not find local identity's keys. Please create them first.");
        }

        //using var signedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var signedPreKeyPublicBytes = localKeys.SignedPreKey.ExportSubjectPublicKeyInfo();
        var signature = localKeys.IdentitySigningKey.SignData(localKeys.SignedPreKey.ExportSubjectPublicKeyInfo(), HashAlgorithmName.SHA256);

        var oneTimeKey = _oneTimeKeyProvider.PopOneTimeKey();
        var localBundle = new ContractsPreKeyBundle
        {
            IdentitySigningKey = ByteString.CopyFrom(localKeys.IdentitySigningKey.ExportSubjectPublicKeyInfo()),
            IdentityAgreementKey = ByteString.CopyFrom(localKeys.IdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo()),
            SignedPreKey = ByteString.CopyFrom(signedPreKeyPublicBytes),
            PreKeySignature = ByteString.CopyFrom(signature),
            OneTimePreKey = oneTimeKey is null ? ByteString.Empty : ByteString.CopyFrom(oneTimeKey.PublicKey.ExportSubjectPublicKeyInfo()),
        };

        using var ephemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        var request = new EstablishSessionRequest
        {
            InitiatorBundle = localBundle,
            InitiatorEphemeralKey = ByteString.CopyFrom(ephemeralKey.PublicKey.ExportSubjectPublicKeyInfo())
        };

        var clientCertificate = await _tlsCertificateService.GetOrCreateTlsCertificateAsync(
            localIdentity.Name, 
            localKeys.IdentitySigningKey.ExportSubjectPublicKeyInfo());
        var handshakeClient = _grpcClientFactory.CreateClient(endpoint, peerName, clientCertificate);
        var response = await handshakeClient.EstablishSessionAsync(request);

        var peer = await _peerRepository.GetByNameAsync(peerName);
        if (peer is null)
        {
            throw new InvalidOperationException($"Peer '{peerName}' was not found after a successful handshake.");
        }

        var remotePublicIdentityKey = response.ResponderBundle.IdentitySigningKey.ToByteArray();

        var remotePeerId = new SessionPeerId(peer.Id.Value);
        var sharedSecret = _orchestrator.CompleteHandshake(response.ResponderBundle, ephemeralKey);

        var channelId = new ChannelId(remotePublicIdentityKey);
        var conversation = (await _conversationRepository.GetByChannelIdAsync(channelId))
                           ?? new ChatConversation(
                               new ChatConversationId(Guid.NewGuid()),
                               channelId,
                               new List<ChatParticipantId>
                               {
                                   new(localIdentity.Id),
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