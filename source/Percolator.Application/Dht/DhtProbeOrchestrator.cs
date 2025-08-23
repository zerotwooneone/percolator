using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Contracts;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Percolator.Application.Sessions;
using Percolator.Cryptography;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Chat.ValueObjects;

namespace Percolator.Application.Dht;

public interface IDhtProbeOrchestrator
{
    Task<FindNodeResponse> ProbeAsync(DnsEndPoint targetEndpoint, string targetIdentityName, string? selfIdentityName = null, CancellationToken cancellationToken = default);

    Task PingAsync(DnsEndPoint targetEndpoint, string targetIdentityName, CancellationToken cancellationToken = default);

    Task<FindNodeResponse> FindNodeAsync(DnsEndPoint targetEndpoint, string targetIdentityName, byte[]? targetPeerId = null, CancellationToken cancellationToken = default);
}

public class DhtProbeOrchestrator : IDhtProbeOrchestrator
{
    private readonly IConversationService _conversationService;
    private readonly IDirectSessionManager _sessionManager;
    private readonly IMessageTransportService _transport;
    private readonly ILogger<DhtProbeOrchestrator> _logger;
    private readonly ActiveIdentityContext _activeIdentityContext;

    public DhtProbeOrchestrator(
        IConversationService conversationService,
        IDirectSessionManager sessionManager,
        IMessageTransportService transport,
        ILogger<DhtProbeOrchestrator> logger,
        ActiveIdentityContext activeIdentityContext)
    {
        _conversationService = conversationService;
        _sessionManager = sessionManager;
        _transport = transport;
        _logger = logger;
        _activeIdentityContext = activeIdentityContext;
    }

    public async Task<FindNodeResponse> ProbeAsync(DnsEndPoint targetEndpoint, string targetIdentityName, string? selfIdentityName = null, CancellationToken cancellationToken = default)
    {
        await PingAsync(targetEndpoint, targetIdentityName, cancellationToken);
        return await FindNodeAsync(targetEndpoint, targetIdentityName, targetPeerId: null, cancellationToken);
    }

    public async Task PingAsync(DnsEndPoint targetEndpoint, string targetIdentityName, CancellationToken cancellationToken = default)
    {
        var conversationId = await EnsureConversationAsync(targetEndpoint, targetIdentityName);
        var envelope = new InternalEnvelope
        {
            DhtEnvelope = new DhtEnvelope { PingRequest = new PingRequest() }
        };
        await SendFireAndForgetAsync(conversationId, envelope, cancellationToken);
    }

    public async Task<FindNodeResponse> FindNodeAsync(DnsEndPoint targetEndpoint, string targetIdentityName, byte[]? targetPeerId = null, CancellationToken cancellationToken = default)
    {
        var conversationId = await EnsureConversationAsync(targetEndpoint, targetIdentityName);

        // Determine target node id
        byte[] nodeIdBytes;
        if (targetPeerId is not null)
        {
            nodeIdBytes = targetPeerId;
        }
        else
        {
            if (_activeIdentityContext.Keys?.IdentitySigningKey is null)
            {
                throw new InvalidOperationException("Active identity signing key not loaded.");
            }
            var signingKeySpki = _activeIdentityContext.Keys.IdentitySigningKey.ExportSubjectPublicKeyInfo();
            nodeIdBytes = System.Security.Cryptography.SHA256.HashData(signingKeySpki);
        }

        var findNodeRequest = new Percolator.Contracts.FindNodeRequest
        {
            TargetPeerId = ByteString.CopyFrom(nodeIdBytes)
        };
        var findNodeEnvelope = new InternalEnvelope
        {
            DhtEnvelope = new DhtEnvelope { FindNodeRequest = findNodeRequest }
        };

        var response = await SendAndReceiveAsync(conversationId, findNodeEnvelope, cancellationToken);
        if (!response.HasResponsePayload)
        {
            _logger.LogInformation("No response payload returned for FindNode.");
            return new FindNodeResponse();
        }

        var respRatchet = new SessionRatchetMessage(response.ResponsePayload.ToByteArray());
        var plaintext = await _sessionManager.ReceiveMessageAsync(new SessionId(conversationId.Value), respRatchet);
        if (plaintext is null)
        {
            _logger.LogWarning("Could not decrypt FindNode response payload.");
            return new FindNodeResponse();
        }

        var internalResp = InternalEnvelope.Parser.ParseFrom(plaintext.Value);
        if (internalResp.ApplicationPayloadCase != InternalEnvelope.ApplicationPayloadOneofCase.DhtEnvelope)
        {
            _logger.LogWarning("Unexpected response envelope type: {Type}", internalResp.ApplicationPayloadCase);
            return new FindNodeResponse();
        }
        var dhtResp = internalResp.DhtEnvelope;
        if (dhtResp.MessageCase != DhtEnvelope.MessageOneofCase.FindNodeResponse)
        {
            _logger.LogWarning("Unexpected DHT response type: {Type}", dhtResp.MessageCase);
            return new FindNodeResponse();
        }
        return dhtResp.FindNodeResponse ?? new FindNodeResponse();
    }

    private async Task<ConversationId> EnsureConversationAsync(DnsEndPoint endpoint, string remotePeerName)
        => await _conversationService.CreateDirectConversationAsync(endpoint, remotePeerName);

    /// <summary>
    /// Encrypts and sends an envelope using repository-resolved peer connection via SendMessageAsync.
    /// Use this for fire-and-forget operations like Ping where no immediate response payload is needed.
    /// </summary>
    private async Task SendFireAndForgetAsync(ConversationId conversationId, InternalEnvelope envelope, CancellationToken cancellationToken)
    {
        var plaintext = new Plaintext(envelope.ToByteArray());
        var (remotePeerId, ratchetMessage) = await _sessionManager.EncryptMessageAsync(new SessionId(conversationId.Value), plaintext);
        _logger.LogInformation("DHT probe sending (fire-and-forget) to peer {PeerId}", remotePeerId);
        await _transport.SendMessageAsync(new Percolator.Identity.PeerId(remotePeerId.Value), conversationId, ratchetMessage);
    }

    private async Task<DeliverOpaqueMessageResponse> SendAndReceiveAsync(ConversationId conversationId, InternalEnvelope envelope, CancellationToken cancellationToken)
    {
        var plaintext = new Plaintext(envelope.ToByteArray());
        var (remotePeerId, ratchetMessage) = await _sessionManager.EncryptMessageAsync(new SessionId(conversationId.Value), plaintext);
        _logger.LogInformation("DHT probe sending (with response) to peer {PeerId}", remotePeerId);
        return await _transport.SendMessageAsync(new Percolator.Identity.PeerId(remotePeerId.Value), conversationId, ratchetMessage, cancellationToken);
    }
}

