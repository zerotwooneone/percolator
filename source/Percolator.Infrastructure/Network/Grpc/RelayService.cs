using Grpc.Core;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Percolator.Application.Chat;
using Percolator.Chat.GroupMembership;
using Percolator.Contracts;
using Percolator.Identity;
using Percolator.Network.Egress;
using Percolator.Network.ValueObjects;

namespace Percolator.Infrastructure.Network.Grpc;

/// <summary>
/// gRPC service implementation for RelayService.
/// Handles bidirectional streaming for authenticated client connections.
/// </summary>
public sealed class RelayService : Contracts.RelayService.RelayServiceBase
{
    private readonly IRelayLiveDispatcher _liveDispatcher;
    private readonly IRelayEgressJobRepository _egressJobRepository;
    private readonly IPeerIdentityQueries _peerIdentityQueries;
    private readonly ILogger<RelayService> _logger;

    public RelayService(
        IRelayLiveDispatcher liveDispatcher,
        IRelayEgressJobRepository egressJobRepository,
        IPeerIdentityQueries peerIdentityQueries,
        ILogger<RelayService> logger)
    {
        _liveDispatcher = liveDispatcher;
        _egressJobRepository = egressJobRepository;
        _peerIdentityQueries = peerIdentityQueries;
        _logger = logger;
    }

    public override async Task<EnqueueOpaqueMessageResponse> EnqueueOpaqueMessage(
        EnqueueOpaqueMessageRequest request,
        ServerCallContext context)
    {
        // Fail-fast Validation
        if (request.DestinationRoutingToken is null || request.DestinationRoutingToken.Length == 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "destination_routing_token is required"));

        if (request.Ciphertext is null || request.Ciphertext.Length == 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "ciphertext is required"));

        // Extract sender's PublicIdentityId from auth headers
        var senderPublicIdentityIdStr = context.RequestHeaders.GetValue("x-percolator-sender-public-identity-id");
        if (senderPublicIdentityIdStr is null)
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Missing sender PublicIdentityId header"));
        }

        var senderPublicIdentityIdBytes = Convert.FromHexString(senderPublicIdentityIdStr);
        var senderPublicIdentityId = new PublicIdentityId(new Guid(senderPublicIdentityIdBytes));

        // Resolve destination routing token to NetworkPeerId
        // For now, we'll treat the routing token as a NetworkPeerId GUID
        var destinationPeerId = new NetworkPeerId(new Guid(request.DestinationRoutingToken.ToByteArray()));

        // Create RelayEgressJob
        var ackId = undefined_ack_id;
        var payloadBytes = request.Ciphertext.ToByteArray();
        var job = new RelayEgressJob(ackId, destinationPeerId, payloadBytes, DateTimeOffset.UtcNow);

        await _egressJobRepository.SaveAsync(job, context.CancellationToken);

        // Fast-Path: Call IRelayLiveDispatcher.PushOpaqueMessageAsync
        // Note: We need to map destinationPeerId to PublicIdentityId for the dispatcher
        await _liveDispatcher.PushOpaqueMessageAsync(
            undefined_destination_public_identity_id,
            ackId,
            payloadBytes,
            context.CancellationToken);

        _logger.LogInformation(
            "Enqueued opaque message for {DestinationPeerId} from {SenderPublicIdentityId}",
            destinationPeerId.Value,
            senderPublicIdentityId);

        return new EnqueueOpaqueMessageResponse { Success = true };
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

        // Create a channel for this client's live messages
        var channel = System.Threading.Channels.Channel.CreateUnbounded<ServerRelayStream>();
        (_liveDispatcher as GrpcRelayLiveDispatcher)?.RegisterStream(senderPublicIdentityId, channel);

        try
        {
            // Flush offline/pending RelayEgressJobs on connect
            await FlushOfflineJobsAsync(senderPublicIdentityId, responseStream, context.CancellationToken);

            // Start background task to process acknowledgments from requestStream
            var ackProcessingTask = ProcessRequestStreamAsync(requestStream, senderPublicIdentityId, context.CancellationToken);

            // Pump live messages from channel to responseStream
            await foreach (var message in channel.Reader.ReadAllAsync(context.CancellationToken))
            {
                await responseStream.WriteAsync(message, context.CancellationToken);
            }

            await ackProcessingTask;
        }
        finally
        {
            // Ensure cleanup on disconnect
            (_liveDispatcher as GrpcRelayLiveDispatcher)?.UnregisterStream(senderPublicIdentityId);
            _logger.LogInformation("Client {PublicIdentityId} disconnected from relay", senderPublicIdentityId);
        }
    }

    private async Task FlushOfflineJobsAsync(
        PublicIdentityId senderPublicIdentityId,
        IServerStreamWriter<ServerRelayStream> responseStream,
        CancellationToken ct)
    {
        // Map PublicIdentityId to NetworkPeerId
        var peerIdentity = await _peerIdentityQueries.GetByPublicIdentityIdAsync(senderPublicIdentityId, ct);
        if (peerIdentity is null)
        {
            _logger.LogWarning("No peer identity found for {PublicIdentityId}", senderPublicIdentityId);
            return;
        }

        var networkPeerId = new NetworkPeerId(peerIdentity.Id.Value);

        // Fetch offline/pending RelayEgressJobs
        var offlineJobs = await _egressJobRepository.GetByDestinationPeerIdAsync(networkPeerId, ct);

        foreach (var job in offlineJobs)
        {
            var serverStream = ServerRelayStream.Parser.ParseFrom(job.PayloadBytes);
            await responseStream.WriteAsync(serverStream, ct);
            _logger.LogDebug("Flushed offline job {JobId} to client {PublicIdentityId}", job.JobId, senderPublicIdentityId);
        }

        _logger.LogInformation("Flushed {Count} offline jobs to client {PublicIdentityId}", offlineJobs.Count, senderPublicIdentityId);
    }

    private async Task ProcessRequestStreamAsync(
        IAsyncStreamReader<ClientRelayStream> requestStream,
        PublicIdentityId senderPublicIdentityId,
        CancellationToken ct)
    {
        await foreach (var clientMessage in requestStream.ReadAllAsync(ct))
        {
            // Listen to the requestStream for ClientRelayStream acknowledgements
            if (clientMessage.PayloadCase == ClientRelayStream.PayloadOneofCase.MessageAck)
            {
                if (clientMessage.MessageAck.AckId is not null)
                {
                    var ackId = new Guid(clientMessage.MessageAck.AckId.ToByteArray());
                    await _egressJobRepository.DeleteAsync(ackId, ct);
                    _logger.LogDebug("Deleted acknowledged job {AckId} for client {PublicIdentityId}", ackId, senderPublicIdentityId);
                }
            }
        }
    }
}
