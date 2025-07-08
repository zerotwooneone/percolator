using System.Security.Cryptography;
using System.Formats.Asn1;
using Google.Protobuf;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Chat;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Sessions;
using IdentityPeerId = Percolator.Identity.PeerId;
using ChatConversation = Percolator.Chat.Conversation;
using ChatConversationId = Percolator.Chat.ValueObjects.ConversationId;
using ChatParticipantId = Percolator.Chat.ValueObjects.ParticipantId;
using SessionPeerId = Percolator.Sessions.PeerId;
using SessionConversationId = Percolator.Sessions.ConversationId;
using ContractsPreKeyBundle = Percolator.Contracts.PreKeyBundle;

namespace Percolator.Application.Sessions;

public class ConversationService : IConversationService
{
    private readonly ActiveIdentityContext _activeIdentityContext;
    private readonly IX3DHManager _cryptoManager;
    private readonly X3DHOrchestrator _orchestrator;
    private readonly DirectSessionManager _sessionManager;
    private readonly IConversationRepository _conversationRepository;
    private readonly IPeerRepository _peerRepository;
    private readonly ILocalPeerProvider _localPeerProvider;
    private readonly ILogger<ConversationService> _logger;

    public ConversationService(
        ActiveIdentityContext activeIdentityContext,
        IX3DHManager cryptoManager,
        X3DHOrchestrator orchestrator,
        DirectSessionManager sessionManager,
        IConversationRepository conversationRepository,
        IPeerRepository peerRepository,
        ILocalPeerProvider localPeerProvider,
        ILogger<ConversationService> logger)
    {
        _activeIdentityContext = activeIdentityContext;
        _cryptoManager = cryptoManager;
        _orchestrator = orchestrator;
        _sessionManager = sessionManager;
        _conversationRepository = conversationRepository;
        _peerRepository = peerRepository;
        _localPeerProvider = localPeerProvider;
        _logger = logger;
    }

    public async Task<ChatConversationId> CreateDirectConversationAsync(string host, int port, string remotePublicIdentityKey)
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
        var signature = _cryptoManager.SignPreKey(localKeys.IdentitySigningKey, new PublicKey(signedPreKeyPublicBytes));
        var localBundle = new ContractsPreKeyBundle
        {
            IdentityAgreementKey = ByteString.CopyFrom(localKeys.IdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo()),
            IdentitySigningKey = ByteString.CopyFrom(localKeys.IdentitySigningKey.ExportSubjectPublicKeyInfo()),
            SignedPreKey = ByteString.CopyFrom(signedPreKeyPublicBytes),
            PreKeySignature = ByteString.CopyFrom(signature.Value),
            OneTimePreKey = ByteString.CopyFrom(localKeys.OneTimePreKeys[0].PublicKey.ExportSubjectPublicKeyInfo())
        };

        using var ephemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        var localPeerId = await _localPeerProvider.GetPeerIdAsync();

        var request = new EstablishSessionRequest
        {
            InitiatorPeerId = localPeerId.Value.ToString(),
            InitiatorBundle = localBundle,
            InitiatorEphemeralKey = ByteString.CopyFrom(ephemeralKey.PublicKey.ExportSubjectPublicKeyInfo())
        };

        using var channel = CreateChannel(host, port, remotePublicIdentityKey);
        var client = new TransportService.TransportServiceClient(channel);

        _logger.LogInformation("Sending EstablishSessionRequest to {Host}:{Port}", host, port);
        var response = await client.EstablishSessionAsync(request);

        var remotePeerId = new SessionPeerId(new Guid(response.ResponderPeerId));
        var sharedSecret = _orchestrator.CompleteHandshake(response.ResponderBundle, ephemeralKey);

        var conversation = new ChatConversation(
            new ChatConversationId(Guid.NewGuid()),
            new List<ChatParticipantId>
            {
                new(localPeerId.Value),
                new(remotePeerId.Value)
            }
        );

        await _conversationRepository.AddAsync(conversation);

        await _sessionManager.EstablishSessionAsInitiatorAsync(
            new SessionConversationId(conversation.Id.Value),
            remotePeerId,
            new OpaquePublicKey(response.ResponderBundle.IdentityAgreementKey.ToByteArray()),
            new OpaquePublicKey(response.ResponderBundle.SignedPreKey.ToByteArray()),
            sharedSecret);

        return conversation.Id;
    }

    public async Task<ChatConversationId?> GetLastActiveConversationIdAsync(Guid peerId)
    {
        var identityPeerId = new IdentityPeerId(peerId);
        var peer = await _peerRepository.GetByIdAsync(identityPeerId);
        return peer?.LastDirectConversationId is null
            ? null
            : new ChatConversationId(peer.LastDirectConversationId.Value);
    }

    private GrpcChannel CreateChannel(string host, int port, string remotePublicIdentityKey)
    {
        var address = $"https://{host}:{port}";
        var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
        {
            _logger.LogInformation("Performing custom server certificate validation. SSL Policy Errors: {SslPolicyErrors}", errors);

            if (cert is null)
            {
                _logger.LogWarning("Server certificate is null. Validation failed.");
                return false;
            }

            _logger.LogInformation("Received server certificate. Subject: {Subject}, Thumbprint: {Thumbprint}", cert.Subject, cert.Thumbprint);

            if (errors != System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors && errors != System.Net.Security.SslPolicyErrors.None)
            {
                _logger.LogWarning("SSL policy reported errors other than chain trust: {SslPolicyErrors}", errors);
            }

            var identityExtension = cert.Extensions[Oids.PeerIdentityKey];
            if (identityExtension is null)
            {
                _logger.LogError("Certificate does not contain the required peer identity extension (OID: {Oid}). Validation failed.", Oids.PeerIdentityKey);
                return false;
            }

            _logger.LogInformation("Found peer identity extension. Validating content.");

            try
            {
                var asnReader = new AsnReader(identityExtension.RawData, AsnEncodingRules.BER);
                var actualPublicKey = asnReader.ReadOctetString();

                if (asnReader.HasData)
                {
                    _logger.LogWarning("ASN.1 reader has extra data after reading the OCTET STRING. The data may be malformed.");
                }

                var validationResult = actualPublicKey.SequenceEqual(Convert.FromBase64String(remotePublicIdentityKey));
                if (validationResult)
                {
                    _logger.LogInformation("Public key in certificate matches expected public key. Validation successful.");
                }
                else
                {
                    _logger.LogError("Public key in certificate does NOT match expected public key. Validation failed.");
                    _logger.LogDebug("Expected Key (Base64): {ExpectedKey}", Convert.ToBase64String(Convert.FromBase64String(remotePublicIdentityKey)));
                    _logger.LogDebug("Actual Key (Base64): {ActualKey}", Convert.ToBase64String(actualPublicKey));
                }
                return validationResult;
            }
            catch (AsnContentException e)
            {
                _logger.LogError(e, "Failed to parse ASN.1 content from certificate extension. Validation failed.");
                return false;
            }
        };

        return GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = handler });
    }
}