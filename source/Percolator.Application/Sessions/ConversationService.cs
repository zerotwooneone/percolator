using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Chat;
using Percolator.Contracts;
using Percolator.Cryptography;
using ChatConversation = Percolator.Chat.Conversation;
using ChatConversationId = Percolator.Chat.ValueObjects.ConversationId;
using ChatParticipantId = Percolator.Chat.ValueObjects.ParticipantId;
using OpaquePublicKey = Percolator.Sessions.OpaquePublicKey;
using SessionPeerId = Percolator.Sessions.PeerId;
using ContractsPreKeyBundle = Percolator.Contracts.PreKeyBundle;

namespace Percolator.Application.Sessions;

public class ConversationService : IConversationService
{
    private readonly ActiveIdentityContext _activeIdentityContext;
    private readonly IX3DHManager _cryptoManager;
    private readonly X3DHOrchestrator _orchestrator;
    private readonly DirectSessionManager _sessionManager;
    private readonly IConversationRepository _conversationRepository;
    private readonly ILocalPeerProvider _localPeerProvider;
    private readonly ILogger<ConversationService> _logger;

    public ConversationService(
        ActiveIdentityContext activeIdentityContext,
        IX3DHManager cryptoManager,
        X3DHOrchestrator orchestrator,
        DirectSessionManager sessionManager,
        IConversationRepository conversationRepository,
        ILocalPeerProvider localPeerProvider,
        ILogger<ConversationService> logger)
    {
        _activeIdentityContext = activeIdentityContext;
        _cryptoManager = cryptoManager;
        _orchestrator = orchestrator;
        _sessionManager = sessionManager;
        _conversationRepository = conversationRepository;
        _localPeerProvider = localPeerProvider;
        _logger = logger;
    }

    public async Task<ChatConversationId> CreateDirectConversationAsync(string host, int port)
    {
        _logger.LogInformation("Attempting to create direct conversation with {Host}:{Port}", host, port);

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
        var signature = _cryptoManager.SignPreKey(localKeys.IdentitySigningKey, signedPreKeyPublicBytes);
        var localBundle = new ContractsPreKeyBundle
        {
            IdentityAgreementKey = ByteString.CopyFrom(localKeys.IdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo()),
            IdentitySigningKey = ByteString.CopyFrom(localKeys.IdentitySigningKey.ExportSubjectPublicKeyInfo()),
            SignedPreKey = ByteString.CopyFrom(signedPreKeyPublicBytes),
            PreKeySignature = ByteString.CopyFrom(signature),
            OneTimePreKey = ByteString.CopyFrom(localKeys.OneTimePreKeys[0].PublicKey.ExportSubjectPublicKeyInfo())
        };

        using var ephemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        var request = new EstablishSessionRequest
        {
            InitiatorBundle = localBundle,
            InitiatorEphemeralKey = ByteString.CopyFrom(ephemeralKey.PublicKey.ExportSubjectPublicKeyInfo())
        };

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        var channel = GrpcChannel.ForAddress($"https://{host}:{port}", new GrpcChannelOptions { HttpHandler = handler });
        var client = new TransportService.TransportServiceClient(channel);

        _logger.LogInformation("Sending EstablishSessionRequest to {Host}:{Port}", host, port);
        var response = await client.EstablishSessionAsync(request);

        var remotePeerId = new SessionPeerId(new Guid(response.ResponderPeerId));
        var sharedSecret = _orchestrator.CompleteHandshake(response.ResponderBundle, ephemeralKey);

        var sessionConversationId = await _sessionManager.EstablishSessionAsync(
            remotePeerId,
            new OpaquePublicKey(response.ResponderBundle.IdentityAgreementKey.ToByteArray()),
            sharedSecret
        );

        _logger.LogInformation("Session established with peer {RemotePeerId}. Conversation ID: {ConversationId}", remotePeerId, sessionConversationId);

        var localPeerId = await _localPeerProvider.GetPeerIdAsync();
        var participants = new List<ChatParticipantId>
        {
            new(localPeerId.Value),
            new(remotePeerId.Value)
        };

        var chatConversationId = new ChatConversationId(sessionConversationId.Value);
        var chatConversation = new ChatConversation(chatConversationId, participants);

        await _conversationRepository.AddAsync(chatConversation);
        _logger.LogInformation("Created and persisted Chat.Conversation with ID {ConversationId}", chatConversationId);

        return chatConversationId;
    }
}
