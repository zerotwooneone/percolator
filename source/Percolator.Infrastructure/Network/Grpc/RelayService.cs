using Grpc.Core;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Percolator.Application.Chat;
using Percolator.Application.Chat.MessageQueue;
using Percolator.Application.Network.RelayHost;
using Percolator.Chat.GroupMembership;
using Percolator.Contracts;
using DeliveryCertificate = Percolator.Chat.GroupLedger.DeliveryCertificate;
using PublicIdentityId = Percolator.Identity.PublicIdentityId;

namespace Percolator.Infrastructure.Network.Grpc;

/// <summary>
/// gRPC service implementation for RelayService.
/// Handles bidirectional streaming for authenticated client connections.
/// </summary>
public sealed class RelayService : Contracts.RelayService.RelayServiceBase
{
    private readonly RelayHostStreamManager _streamManager;
    private readonly IMessageQueueQueries _messageQueueQueries;
    private readonly IMessageQueueRepository _messageQueueRepository;
    private readonly IDeliveryCertificateStore _deliveryCertificateStore;
    private readonly IPeerIdentityQueries _peerIdentityQueries;
    private readonly ISelfCertificateService _selfCertificateService;
    private readonly ILogger<RelayService> _logger;

    public RelayService(
        RelayHostStreamManager streamManager,
        IMessageQueueQueries messageQueueQueries,
        IMessageQueueRepository messageQueueRepository,
        IDeliveryCertificateStore deliveryCertificateStore,
        IPeerIdentityQueries peerIdentityQueries,
        ISelfCertificateService selfCertificateService,
        ILogger<RelayService> logger)
    {
        _streamManager = streamManager;
        _messageQueueQueries = messageQueueQueries;
        _messageQueueRepository = messageQueueRepository;
        _deliveryCertificateStore = deliveryCertificateStore;
        _peerIdentityQueries = peerIdentityQueries;
        _selfCertificateService = selfCertificateService;
        _logger = logger;
    }

    public override async Task<EnqueueOpaqueMessageResponse> EnqueueOpaqueMessage(
        EnqueueOpaqueMessageRequest request,
        ServerCallContext context)
    {
        // TODO: Implement opaque message enqueue for 1:1 relay
        // This is the authenticated drop-off point for 1:1 messages
        _logger.LogWarning("EnqueueOpaqueMessage not yet implemented");
        return new EnqueueOpaqueMessageResponse { Success = false };
    }

    public override async Task ConnectRelay(
        IAsyncStreamReader<ClientRelayStream> requestStream,
        IServerStreamWriter<ServerRelayStream> responseStream,
        ServerCallContext context)
    {
        // Extract caller's PublicIdentityId from context (set by DeliveryCertificateAuthInterceptor)
        var senderPublicIdentityIdStr = context.RequestHeaders.GetValue("x-percolator-sender-public-identity-id");
        if (senderPublicIdentityIdStr is null)
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Missing sender PublicIdentityId header"));
        }

        var senderPublicIdentityIdBytes = Convert.FromHexString(senderPublicIdentityIdStr);
        var senderPublicIdentityId = new PublicIdentityId(new Guid(senderPublicIdentityIdBytes));

        // Determine if sender is a local self identity or a remote peer
        var publicIdentityLookup = await _peerIdentityQueries.GetPeerOrSelfIdByPublicIdentityIdAsync(senderPublicIdentityId, context.CancellationToken);
        if (publicIdentityLookup is null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Sender identity not found"));
        }

        // Get the delivery certificate for this client to check expiry
        DeliveryCertificate? certificate;
        if (publicIdentityLookup.Value.IsPeer)
        {
            // For remote peers, use IDeliveryCertificateStore (certificates issued by external relays)
            // TODO: Determine which ChatSelfId to use for certificate lookup
            certificate = await _deliveryCertificateStore.GetCertificateAsync(
                note("TODO: Determine SelfId"), // ChatSelfId - need to determine
                new ChatPeerId(publicIdentityLookup.Value.PeerId.Value), // ChatPeerId - need to determine relay peer ID
                context.CancellationToken);
        }
        else
        {
            // For local self identities, use ISelfCertificateService (self-generated certificates)
            certificate = await _selfCertificateService.GenerateValidCertAsync(publicIdentityLookup.Value.SelfId, context.CancellationToken);
        }

        if (certificate is null)
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "No delivery certificate found"));
        }

        // Register the client's stream using PublicIdentityId (not PeerId or ChatSelfId)
        _streamManager.RegisterClient(senderPublicIdentityId, responseStream);

        try
        {
            // Flush any pending messages on connect
            await FlushPendingMessagesAsync(senderPublicIdentityId, responseStream, context.CancellationToken);

            // Process incoming stream messages with periodic certificate expiry checking
            var certificateCheckInterval = TimeSpan.FromSeconds(30);
            var lastCertificateCheck = DateTimeOffset.UtcNow;

            await foreach (var clientMessage in requestStream.ReadAllAsync(context.CancellationToken))
            {
                // Periodic certificate expiry check
                var now = DateTimeOffset.UtcNow;
                if (now - lastCertificateCheck >= certificateCheckInterval)
                {
                    if (now >= certificate.ExpiresAtUtc)
                    {
                        _logger.LogWarning("Certificate expired for client {PublicIdentityId}, dropping stream", senderPublicIdentityId);
                        throw new RpcException(new Status(StatusCode.Unauthenticated, "Delivery certificate expired"));
                    }
                    lastCertificateCheck = now;
                }

                await ProcessClientMessageAsync(clientMessage, senderPublicIdentityId, context.CancellationToken);
            }
        }
        finally
        {
            // Ensure cleanup on disconnect
            _streamManager.RemoveClient(senderPublicIdentityId);
            _logger.LogInformation("Client {PublicIdentityId} disconnected from relay", senderPublicIdentityId);
        }
    }

    private async Task FlushPendingMessagesAsync(
        PublicIdentityId senderPublicIdentityId,
        IServerStreamWriter<ServerRelayStream> responseStream,
        CancellationToken ct)
    {
        var pendingMessages = await _messageQueueQueries.FetchAsync(senderPublicIdentityId, maxCount: 100, ct);

        foreach (var (ackId, blob) in pendingMessages)
        {
            var delivery = new ServerRelayStream
            {
                OpaqueDelivery = new OpaqueMessageDelivery
                {
                    AckId = ByteString.CopyFrom(ackId.ToByteArray()),
                    OpaquePayload = ByteString.CopyFrom(blob.Span)
                }
            };

            await responseStream.WriteAsync(delivery, ct);
            _logger.LogDebug("Flushed pending message {AckId} to client {PublicIdentityId}", ackId, senderPublicIdentityId);
        }

        _logger.LogInformation("Flushed {Count} pending messages to client {PublicIdentityId}", pendingMessages.Count, senderPublicIdentityId);
    }

    private async Task ProcessClientMessageAsync(
        ClientRelayStream clientMessage,
        PublicIdentityId senderPublicIdentityId,
        CancellationToken ct)
    {
        switch (clientMessage.PayloadCase)
        {
            case ClientRelayStream.PayloadOneofCase.MessageAck:
                // Delete the acknowledged message from the queue
                if (Guid.TryParse(clientMessage.MessageAck.AckId.ToString(), out var ackId))
                {
                    var deleted = await _messageQueueRepository.DeleteByAckIdAsync(ackId, ct);
                    if (deleted)
                    {
                        _logger.LogDebug("Deleted acknowledged message {AckId} for client {PublicIdentityId}", ackId, senderPublicIdentityId);
                    }
                }
                break;
            default:
                _logger.LogWarning("Unknown message type from client {PublicIdentityId}", senderPublicIdentityId);
                break;
        }
    }
}
