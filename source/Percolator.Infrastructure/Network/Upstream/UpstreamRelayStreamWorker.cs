using Grpc.Core;
using Google.Protobuf;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Percolator.Application.Chat;
using Percolator.Application.Network;
using Percolator.Contracts;
using Percolator.Identity;
using Percolator.Network;
using Percolator.Chat.GroupMembership;

namespace Percolator.Infrastructure.Network.Upstream;

/// <summary>
/// Background worker that manages this node's continuous authenticated ingress connection
/// to external Relay Hosts via the bidirectional ConnectRelay stream.
/// </summary>
public sealed class UpstreamRelayStreamWorker : BackgroundService
{
    private readonly IRelayPeerQueries _relayPeerQueries;
    private readonly IPeerRoutingProfileRepository _peerRoutingProfileRepository;
    private readonly IProfileRoutePlanner _profileRoutePlanner;
    private readonly IPeerGrpcChannelFactory _channelFactory;
    private readonly IDeliveryCertificateStore _deliveryCertificateStore;
    private readonly ISelfCertificateService _selfCertificateService;
    private readonly ISelfIdentityQueries _selfIdentityQueries;
    private readonly IOpaqueMessageDeliverer _opaqueMessageDeliverer;
    private readonly IGroupStreamIngressProcessor _groupStreamIngressProcessor;
    private readonly ILogger<UpstreamRelayStreamWorker> _logger;
    private readonly CancellationTokenSource _shutdownCts = new();

    public UpstreamRelayStreamWorker(
        IRelayPeerQueries relayPeerQueries,
        IPeerRoutingProfileRepository peerRoutingProfileRepository,
        IProfileRoutePlanner profileRoutePlanner,
        IPeerGrpcChannelFactory channelFactory,
        IDeliveryCertificateStore deliveryCertificateStore,
        ISelfCertificateService selfCertificateService,
        ISelfIdentityQueries selfIdentityQueries,
        IOpaqueMessageDeliverer opaqueMessageDeliverer,
        IGroupStreamIngressProcessor groupStreamIngressProcessor,
        ILogger<UpstreamRelayStreamWorker> logger)
    {
        _relayPeerQueries = relayPeerQueries;
        _peerRoutingProfileRepository = peerRoutingProfileRepository;
        _profileRoutePlanner = profileRoutePlanner;
        _channelFactory = channelFactory;
        _deliveryCertificateStore = deliveryCertificateStore;
        _selfCertificateService = selfCertificateService;
        _selfIdentityQueries = selfIdentityQueries;
        _opaqueMessageDeliverer = opaqueMessageDeliverer;
        _groupStreamIngressProcessor = groupStreamIngressProcessor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("UpstreamRelayStreamWorker starting");

        // Get all relay connections with their associated self identities
        var relayConnections = (await _relayPeerQueries.GetAllAsync(stoppingToken)).ToArray();
        
        _logger.LogInformation("Found {Count} relay connections", relayConnections.Count());

        // Launch a background task for each relay connection
        var relayTasks = new List<Task>();
        foreach (var connection in relayConnections)
        {
            var selfId = connection.SelfId;
            var selfDeviceId = connection.SelfDeviceId;
            var relayPeerId = new NetworkPeerId(connection.RelayPeerId);
            var task = Task.Run(() => ManageRelayConnectionAsync(selfId, selfDeviceId, relayPeerId, _shutdownCts.Token), _shutdownCts.Token);
            relayTasks.Add(task);
        }

        // Wait for all tasks to complete (on shutdown)
        await Task.WhenAll(relayTasks);

        _logger.LogInformation("UpstreamRelayStreamWorker stopped");
    }

    private async Task ManageRelayConnectionAsync(uint selfId, uint selfDeviceId, NetworkPeerId relayPeerId, CancellationToken ct)
    {
        var backoffSeconds = 1;
        const int maxBackoffSeconds = 300; // 5 minutes max backoff

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndProcessStreamAsync(selfId, selfDeviceId, relayPeerId, ct);
                backoffSeconds = 1; // Reset backoff on successful connection
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Relay connection to {RelayPeerId} cancelled", relayPeerId);
                break;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
            {
                _logger.LogWarning(ex, "Authentication failed connecting to relay {RelayPeerId}, will retry with backoff", relayPeerId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error connecting to relay {RelayPeerId}, will retry with backoff", relayPeerId);
            }

            // Exponential backoff
            await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), ct);
            backoffSeconds = Math.Min(backoffSeconds * 2, maxBackoffSeconds);
        }
    }

    private async Task ConnectAndProcessStreamAsync(uint selfId, uint selfDeviceId, NetworkPeerId relayPeerId, CancellationToken ct)
    {
        // Get relay profile to find endpoint
        var relayProfile = await _peerRoutingProfileRepository.GetByIdAsync(relayPeerId, ct);
        if (relayProfile == null)
        {
            _logger.LogWarning("No profile found for relay {RelayPeerId}", relayPeerId);
            return;
        }

        // Use route planner to select the best endpoint
        var routeSelection = _profileRoutePlanner.SelectRoute(relayProfile);
        var endpoint = routeSelection.Endpoint;
        _logger.LogInformation("Connecting to relay {RelayPeerId} at {Host}:{Port} for self {SelfId}", relayPeerId, endpoint.EndPoint.Host, endpoint.EndPoint.Port, selfId);

        // Get delivery certificate
        var certificate = await _deliveryCertificateStore.GetCertificateAsync(
            new ChatSelfId(selfId),
            new ChatPeerId(relayPeerId.Value),
            ct);

        if (certificate == null)
        {
            _logger.LogWarning("No delivery certificate found for self {SelfId} to relay {RelayId}", selfId, relayPeerId);
            return;
        }

        // Check certificate expiry
        if (DateTimeOffset.UtcNow >= certificate.ExpiresAtUtc)
        {
            _logger.LogWarning("Delivery certificate expired for self {SelfId} to relay {RelayId}", selfId, relayPeerId);
            return;
        }

        var publicIdentityId = await _selfIdentityQueries.GetSelfIdentityPublicKeyAsync(new SelfId(selfId), ct);
        if(publicIdentityId is null)
            throw new InvalidOperationException($"No public identity found for self {selfId}");
        
        // Get authentication headers from certificate service
        var authHeaders = await _selfCertificateService.GetRelayAuthenticationHeadersAsync(new ChatSelfId(selfId), publicIdentityId, ct);
        if (authHeaders == null)
        {
            _logger.LogWarning("Failed to get authentication headers for self {SelfId}", selfId);
            return;
        }

        // Create gRPC channel and client
        using var channel = _channelFactory.CreateChannel(endpoint.EndPoint);
        var client = new RelayService.RelayServiceClient(channel);

        var headers = new Metadata
        {
            { "x-percolator-sender-public-identity-id", authHeaders.PublicIdentityId },
            { "x-percolator-timestamp", authHeaders.Timestamp },
            { "x-percolator-signature", authHeaders.Signature }
        };

        var callOptions = new CallOptions(headers: headers, cancellationToken: ct);

        // Start bidirectional stream
        using var call = client.ConnectRelay(callOptions);

        // Process incoming messages
        await foreach (var serverMessage in call.ResponseStream.ReadAllAsync(ct))
        {
            await ProcessServerMessageAsync(serverMessage, call.RequestStream, publicIdentityId, selfDeviceId, ct);
        }
    }

    private async Task ProcessServerMessageAsync(
        ServerRelayStream serverMessage,
        IAsyncStreamWriter<ClientRelayStream> requestStream,
        PublicIdentityId selfIdentityId,
        uint selfDeviceId,
        CancellationToken ct)
    {
        switch (serverMessage.PayloadCase)
        {
            case ServerRelayStream.PayloadOneofCase.GroupDelivery:
                await ProcessGroupDeliveryAsync(serverMessage.GroupDelivery, selfIdentityId, selfDeviceId, ct);
                break;

            case ServerRelayStream.PayloadOneofCase.OpaqueDelivery:
                await ProcessOpaqueDeliveryAsync(serverMessage.OpaqueDelivery, requestStream, ct);
                break;

            default:
                _logger.LogWarning("Unknown server message type: {MessageType}", serverMessage.PayloadCase);
                break;
        }
    }

    private async Task ProcessGroupDeliveryAsync(
        GroupMessageDelivery groupDelivery,
        PublicIdentityId selfIdentityId,
        uint deviceId,
        CancellationToken ct)
    {
        var conversationId = new Guid(groupDelivery.ConversationId.ToByteArray());
        var epoch = groupDelivery.Epoch;
        var ciphertext = groupDelivery.Ciphertext.ToByteArray();
        var senderPresentation = groupDelivery.SenderPresentation.ToByteArray();

        // Get self identity info for the processor
        var selfId = await _selfIdentityQueries.GetSelfIdByPublicIdentityIdAsync(selfIdentityId, ct);
        if (selfId == null)
        {
            _logger.LogWarning("Self identity not found for {PublicIdentityId}", selfIdentityId);
            return;
        }

        await _groupStreamIngressProcessor.ProcessGroupMessageAsync(
            conversationId,
            selfIdentityId.Value,
            epoch,
            ciphertext,
            new ChatSelfId(selfId.Value.Value),
            deviceId,
            DateTimeOffset.UtcNow,
            ct);
    }

    private async Task ProcessOpaqueDeliveryAsync(
        OpaqueMessageDelivery opaqueDelivery,
        IAsyncStreamWriter<ClientRelayStream> requestStream,
        CancellationToken ct)
    {
        var opaquePayload = opaqueDelivery.OpaquePayload.ToByteArray();
        var ackId = new Guid(opaqueDelivery.AckId.ToByteArray());

        // Deliver the opaque message
        var delivered = await _opaqueMessageDeliverer.DeliverAsync(opaquePayload, ct);

        if (delivered)
        {
            // Send ack back upstream
            var ack = new ClientRelayStream
            {
                MessageAck = new MessageAck
                {
                    AckId = ByteString.CopyFrom(ackId.ToByteArray())
                }
            };

            await requestStream.WriteAsync(ack, ct);
            _logger.LogDebug("Sent ack for message {AckId}", ackId);
        }
        else
        {
            _logger.LogWarning("Failed to deliver opaque message {AckId}", ackId);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("UpstreamRelayStreamWorker stopping");
        _shutdownCts.Cancel();
        await base.StopAsync(cancellationToken);
    }
}
