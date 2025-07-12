using System.Security.Cryptography;
using System.Formats.Asn1;
using System.Net;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using ChatConversation = Percolator.Chat.Conversation;
using ChatConversationId = Percolator.Chat.ValueObjects.ConversationId;
using ChatParticipantId = Percolator.Chat.ValueObjects.ParticipantId;
using IdentityPeerId = Percolator.Identity.PeerId;
using NetworkPeerId = Percolator.Network.PeerId;
using SessionPeerId = Percolator.Sessions.PeerId;
using SessionConversationId = Percolator.Sessions.ConversationId;
using SessionSharedSecret = Percolator.Sessions.SharedSecret;
using ContractsPreKeyBundle = Percolator.Contracts.PreKeyBundle;

namespace Percolator.Application.Sessions;

public class ConversationService : IConversationService
{
    private readonly ILogger<ConversationService> _logger;
    private readonly IX3DHOrchestrator _orchestrator;
    private readonly IDirectSessionManager _sessionManager;
    private readonly IConversationRepository _conversationRepository;
    private readonly IPeerRepository _peerRepository;
    private readonly IPeerConnectionRepository _peerConnectionRepository;
    private readonly ITlsCertificateService _tlsCertificateService;
    private readonly IOneTimeKeyProvider _oneTimeKeyProvider;
    private readonly ActiveIdentityContext _activeIdentityContext;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPeerTrustManager _peerTrustManager;

    public ConversationService(
        ILogger<ConversationService> logger,
        IX3DHOrchestrator orchestrator,
        IDirectSessionManager sessionManager,
        IConversationRepository conversationRepository,
        IPeerRepository peerRepository,
        IPeerConnectionRepository peerConnectionRepository,
        ITlsCertificateService tlsCertificateService,
        IOneTimeKeyProvider oneTimeKeyProvider,
        ActiveIdentityContext activeIdentityContext,
        IHttpClientFactory httpClientFactory,
        IPeerTrustManager peerTrustManager)
    {
        _logger = logger;
        _orchestrator = orchestrator;
        _sessionManager = sessionManager;
        _conversationRepository = conversationRepository;
        _peerRepository = peerRepository;
        _peerConnectionRepository = peerConnectionRepository;
        _tlsCertificateService = tlsCertificateService;
        _oneTimeKeyProvider = oneTimeKeyProvider;
        _activeIdentityContext = activeIdentityContext;
        _httpClientFactory = httpClientFactory;
        _peerTrustManager = peerTrustManager;
    }

    public async Task<ChatConversationId> CreateDirectConversationAsync(DnsEndPoint endpoint, string peerName)
    {
        _logger.LogInformation("Attempting to create direct conversation with {PeerName} at {Endpoint}", peerName, endpoint);

        var localIdentity = _activeIdentityContext.Identity ?? throw new InvalidOperationException("Cannot create a conversation without an active identity.");
        var localKeys = _activeIdentityContext.Keys ?? throw new InvalidOperationException("Cannot create a conversation without active keys.");

        // 1. Generate the ephemeral key for this handshake
        using var ephemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        var signedPreKeyPublicBytes = localKeys.SignedPreKey.ExportSubjectPublicKeyInfo();
        var signature = localKeys.IdentitySigningKey.SignData(signedPreKeyPublicBytes, HashAlgorithmName.SHA256);
        var oneTimeKey = _oneTimeKeyProvider.PopOneTimeKey();

        // 2. Build the correct request object
        var request = new EstablishSessionRequest
        {
            InitiatorBundle = new ContractsPreKeyBundle
            {
                IdentitySigningKey = ByteString.CopyFrom(localKeys.IdentitySigningKey.ExportSubjectPublicKeyInfo()),
                SignedPreKey = ByteString.CopyFrom(signedPreKeyPublicBytes),
                PreKeySignature = ByteString.CopyFrom(signature),
                OneTimePreKey = oneTimeKey is null ? ByteString.Empty : ByteString.CopyFrom(oneTimeKey.PublicKey.ExportSubjectPublicKeyInfo())
            },
            InitiatorEphemeralKey = ByteString.CopyFrom(ephemeralKey.PublicKey.ExportSubjectPublicKeyInfo())
        };

        var httpClient = _httpClientFactory.CreateClient("percolator-grpc");
        var channel = GrpcChannel.ForAddress($"https://{endpoint.Host}:{endpoint.Port}", new GrpcChannelOptions { HttpClient = httpClient });
        var client = new TransportService.TransportServiceClient(channel);

        try
        {
            _logger.LogInformation("Sending EstablishSession request to {Endpoint}", endpoint);
            var response = await client.EstablishSessionAsync(request);
            _logger.LogInformation("Successfully received EstablishSession response from {Endpoint}", endpoint);

            var peer = await _peerRepository.GetByNameAsync(peerName);
            if (peer is null)
            {
                throw new InvalidOperationException($"Could not find peer {peerName} after handshake. This should not happen.");
            }

            // Persist the DirectMessagePublicKey now that we have it
            var directMessagePublicKey = new DirectMessagePublicKey(response.ResponderBundle.IdentityAgreementKey.ToByteArray());
            await _peerConnectionRepository.UpdateDirectMessagePublicKeyAsync(new NetworkPeerId(peer.Id.Value), directMessagePublicKey);

            // 3. Correctly complete the handshake
            var sharedSecret = _orchestrator.CompleteHandshake(response.ResponderBundle, ephemeralKey);

            var conversation = new ChatConversation(
                new ChatConversationId(Guid.NewGuid()),
                new ChannelId(response.ResponderBundle.IdentitySigningKey.ToByteArray()),
                new List<ChatParticipantId> { new(localIdentity.Id), new(peer.Id.Value) },
                new List<Message>(),
                peerName);

            await _conversationRepository.AddAsync(conversation);

            await _sessionManager.EstablishSessionAsInitiatorAsync(
                new SessionConversationId(conversation.Id.Value),
                new SessionPeerId(peer.Id.Value),
                new SessionIdentityKey(response.ResponderBundle.IdentityAgreementKey.ToByteArray()),
                new SessionRatchetKey(response.ResponderBundle.SignedPreKey.ToByteArray()),
                new SessionSharedSecret(sharedSecret.Value));

            _logger.LogInformation("Successfully established session and created conversation {ConversationId}", conversation.Id.Value);

            return conversation.Id;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable && ex.Status.Detail.Contains("is not trusted"))
        {
            _logger.LogWarning(ex, "TLS handshake failed for peer at {Endpoint}. Attempting Trust on First Use (TOFU).", endpoint);

            // Use the TofuHandler to get the server certificate
            var remoteCert = ex.Status.Detail.Contains("certificate") ? new X509Certificate2(ex.Status.Detail.Split("certificate:")[1].Trim()) : null;
            if (remoteCert is null)
            {
                _logger.LogError("Failed to retrieve server certificate for TOFU from {Endpoint}.", endpoint);
                throw;
            }

            var tlsCertificate = new TlsCertificate(remoteCert.Export(X509ContentType.Cert));

            var existingConnection = await _peerConnectionRepository.GetByTlsCertificateAsync(tlsCertificate);
            if (existingConnection is not null)
            {
                // This should not happen. If we have a connection record, the certificate should be trusted.
                throw new InvalidOperationException("Peer connection already exists, but TLS certificate is not trusted.");
            }

            // 1. Create and save the new Peer and PeerConnection
            var identityPeerId = new Percolator.Identity.PeerId(Guid.NewGuid());
            var peer = new Percolator.Identity.Peer(identityPeerId, peerName);
            await _peerRepository.AddAsync(peer);

            var newConnection = new PeerConnection(
                new NetworkPeerId(peer.Id.Value),
                null, // DirectMessagePublicKey is not yet known
                new[] { new GrpcEndPoint(new DnsEndPoint(endpoint.Host, endpoint.Port), DateTimeOffset.UtcNow) },
                new[] { tlsCertificate },
                DateTimeOffset.UtcNow);
            await _peerConnectionRepository.SaveAsync(newConnection);

            // 2. Add the certificate to the trust store so the next attempt succeeds
            await _peerTrustManager.AddTrustedPeer(remoteCert);

            // 3. Retry the connection
            // Create a handler that specifically trusts the remote certificate for the retry
            var handler = new SocketsHttpHandler
            {
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) =>
                    {
                        if (certificate is null) return false;
                        return certificate.Equals(remoteCert);
                    }
                }
            };
            var retryHttpClient = new HttpClient(handler);
            var retryChannel = GrpcChannel.ForAddress($"https://{endpoint.Host}:{endpoint.Port}", new GrpcChannelOptions { HttpClient = retryHttpClient });
            var retryClient = new TransportService.TransportServiceClient(retryChannel);

            var response = await retryClient.EstablishSessionAsync(request);
            
            // Persist the DirectMessagePublicKey now that we have it
            var directMessagePublicKey = new DirectMessagePublicKey(response.ResponderBundle.IdentityAgreementKey.ToByteArray());
            await _peerConnectionRepository.UpdateDirectMessagePublicKeyAsync(new NetworkPeerId(peer.Id.Value), directMessagePublicKey);

            // 4. Correctly complete the handshake on retry
            var sharedSecret = _orchestrator.CompleteHandshake(response.ResponderBundle, ephemeralKey);

            var conversation = new ChatConversation(
                new ChatConversationId(Guid.NewGuid()),
                new ChannelId(response.ResponderBundle.IdentitySigningKey.ToByteArray()),
                new List<ChatParticipantId> { new(_activeIdentityContext.Identity!.Id), new(peer.Id.Value) },
                new List<Message>(),
                peerName);

            await _conversationRepository.AddAsync(conversation);

            await _sessionManager.EstablishSessionAsInitiatorAsync(
                new SessionConversationId(conversation.Id.Value),
                new SessionPeerId(peer.Id.Value),
                new SessionIdentityKey(response.ResponderBundle.IdentityAgreementKey.ToByteArray()),
                new SessionRatchetKey(response.ResponderBundle.SignedPreKey.ToByteArray()),
                new SessionSharedSecret(sharedSecret.Value));

            _logger.LogInformation("Successfully established session and created conversation {ConversationId}", conversation.Id.Value);

            return conversation.Id;
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "An unexpected gRPC error occurred while creating a direct conversation.");
            throw;
        }
    }

    public Task<ChatConversationId?> FindExistingConversationAsync(Percolator.Identity.PeerId peerId)
    {
        //todo: create Session domain peer that tracks last active conversation
        return Task.FromResult<ChatConversationId?>(null);
    }
}