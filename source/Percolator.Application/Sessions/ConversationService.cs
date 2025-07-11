using System.Security.Cryptography;
using System.Formats.Asn1;
using System.Net;
using Google.Protobuf;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Chat;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;
using NetworkPeerId = Percolator.Network.PeerId;
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
    private readonly X3DHOrchestrator _orchestrator;
    private readonly DirectSessionManager _sessionManager;
    private readonly IConversationRepository _conversationRepository;
    private readonly IPeerRepository _peerRepository;
    private readonly ILocalPeerProvider _localPeerProvider;
    private readonly ILogger<ConversationService> _logger;
    private readonly IPeerConnectionRepository _peerConnectionRepository;

    public ConversationService(ActiveIdentityContext activeIdentityContext,
        IX3DHManager cryptoManager,
        X3DHOrchestrator orchestrator,
        DirectSessionManager sessionManager,
        IConversationRepository conversationRepository,
        IPeerRepository peerRepository,
        ILocalPeerProvider localPeerProvider,
        ILogger<ConversationService> logger, 
        IPeerConnectionRepository peerConnectionRepository)
    {
        _activeIdentityContext = activeIdentityContext;
        _orchestrator = orchestrator;
        _sessionManager = sessionManager;
        _conversationRepository = conversationRepository;
        _peerRepository = peerRepository;
        _localPeerProvider = localPeerProvider;
        _logger = logger;
        _peerConnectionRepository = peerConnectionRepository;
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

        using var channel = CreateChannel(endpoint, tlsCertificate);
        var client = new TransportService.TransportServiceClient(channel);

        _logger.LogInformation("Sending EstablishSessionRequest to {endpoint}", endpoint);
        var response = await client.EstablishSessionAsync(request);

        Peer? peer = null;

        // 1. Look up peer by the long-term public key provided.
        _logger.LogInformation("Attempting to find peer by name: {PeerName}", peerName);
        peer = await _peerRepository.GetByNameAsync(peerName);
        
        // 2. If not found, look up by the key in the responder's bundle.
        if (peer is null)
        {
            throw new InvalidOperationException($"Could not find peer with name {peerName}. Please provide the peer's TLS certificate.");
        }
        
        var peerConnection = await _peerConnectionRepository.GetByIdAsync(new NetworkPeerId(peer.Id.Value));
        if (peerConnection is null)
        {
            throw new InvalidOperationException($"Could not find peer connection info for peer with name {peerName}.");
        }

        var remotePeerId = new SessionPeerId(peer.Id.Value);
        var sharedSecret = _orchestrator.CompleteHandshake(response.ResponderBundle, ephemeralKey);

        var conversationId = new ChatConversationId(Guid.NewGuid());
        var conversation = new ChatConversation(
            conversationId,
            new List<ChatParticipantId>
            {
                new(localPeerId.Value),
                new(remotePeerId.Value)
            });

        await _conversationRepository.AddAsync(conversation);

        await _sessionManager.EstablishSessionAsInitiatorAsync(
            new SessionConversationId(conversationId.Value),
            remotePeerId,
            new SessionIdentityKey(response.ResponderBundle.IdentityAgreementKey.ToByteArray()),
            new SessionRatchetKey(response.ResponderBundle.SignedPreKey.ToByteArray()),
            sharedSecret);

        _logger.LogInformation("Successfully established session and created conversation {ConversationId}", conversationId);

        return conversationId;
    }

    public Task<ChatConversationId?> GetLastActiveConversationIdAsync(Guid peerId)
    {
        //todo: create Session domain peer that tracks last active conversation
        return Task.FromResult<ChatConversationId?>(null);
    }

    private GrpcChannel CreateChannel(DnsEndPoint endpoint, TlsCertificate? tlsCertificate = null)
    {
        var address = $"https://{endpoint.Host}:{endpoint.Port}";
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

                var validationResult = tlsCertificate is not null && actualPublicKey.SequenceEqual(tlsCertificate.Value);
                if (validationResult)
                {
                    _logger.LogInformation("Public key in certificate matches expected public key. Validation successful.");
                }
                else if (tlsCertificate is not null)
                {
                    _logger.LogError("Public key in certificate does NOT match expected public key. Validation failed.");
                    _logger.LogDebug("Expected Key (Base64): {ExpectedKey}", Convert.ToBase64String(tlsCertificate.Value));
                    _logger.LogDebug("Actual Key (Base64): {ActualKey}", Convert.ToBase64String(actualPublicKey));
                    return false;
                }
                return true;
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