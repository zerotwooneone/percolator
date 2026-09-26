using Grpc.Core;
using Google.Protobuf;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Percolator.Application.Chat;
using Percolator.Contracts;
using Percolator.Network;
using Percolator.Network.Egress;
using Percolator.Network.ValueObjects;
using Percolator.Infrastructure.Network;
using System.Net;
using Percolator.Chat.GroupMembership;

namespace Percolator.Infrastructure.Egress;

/// <summary>
/// Background worker that processes network egress jobs and dispatches them via gRPC.
/// Handles both direct P2P routing and relay routing with split authorization for privacy.
/// </summary>
public sealed class NetworkEgressWorker : BackgroundService
{
    private readonly INetworkEgressJobRepository _repository;
    private readonly ITransportServiceClient _transportServiceClient;
    private readonly IRelayServiceClient _relayServiceClient;
    private readonly IAnonymousGroupServiceClient _anonymousGroupServiceClient;
    private readonly IPeerGrpcChannelFactory _channelFactory;
    private readonly IPeerRoutingProfileRepository _peerRoutingProfileRepository;
    private readonly IDeliveryCertificateStore _deliveryCertificateStore;
    private readonly ILogger<NetworkEgressWorker> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(5);

    public NetworkEgressWorker(
        INetworkEgressJobRepository repository,
        ITransportServiceClient transportServiceClient,
        IRelayServiceClient relayServiceClient,
        IAnonymousGroupServiceClient anonymousGroupServiceClient,
        IPeerGrpcChannelFactory channelFactory,
        IPeerRoutingProfileRepository peerRoutingProfileRepository,
        IDeliveryCertificateStore deliveryCertificateStore,
        ILogger<NetworkEgressWorker> logger)
    {
        _repository = repository;
        _transportServiceClient = transportServiceClient;
        _relayServiceClient = relayServiceClient;
        _anonymousGroupServiceClient = anonymousGroupServiceClient;
        _channelFactory = channelFactory;
        _peerRoutingProfileRepository = peerRoutingProfileRepository;
        _deliveryCertificateStore = deliveryCertificateStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("NetworkEgressWorker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingJobsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing network egress jobs");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }

        _logger.LogInformation("NetworkEgressWorker stopped");
    }

    internal async Task ProcessPendingJobsAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var pendingJobs = await _repository.GetPendingJobsAsync(now, ct);

        if (pendingJobs.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Processing {Count} pending network egress jobs", pendingJobs.Count);

        foreach (var job in pendingJobs)
        {
            await ProcessJobAsync(job, ct);
        }
    }

    private async Task ProcessJobAsync(NetworkEgressJob job, CancellationToken ct)
    {
        try
        {
            bool success = job.RoutePreference switch
            {
                RoutePreference.Direct => await DispatchDirectAsync(job, ct),
                RoutePreference.Relay => await DispatchRelayAsync(job, ct),
                RoutePreference.Any => await DispatchAnyAsync(job, ct),
                _ => throw new InvalidOperationException($"Unknown route preference: {job.RoutePreference}")
            };

            if (success)
            {
                job.MarkSent();
                await _repository.SaveAsync(job, ct);
                await _repository.DeleteAsync(job.JobId, ct);
                _logger.LogInformation("Successfully dispatched egress job {JobId}", job.JobId.Value);
            }
            else
            {
                job.RecordFailure(DateTimeOffset.UtcNow);
                await _repository.SaveAsync(job, ct);
                _logger.LogWarning("Failed to dispatch egress job {JobId}, attempt {Attempt}", job.JobId.Value, job.AttemptCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing egress job {JobId}", job.JobId.Value);
            job.RecordFailure(DateTimeOffset.UtcNow);
            await _repository.SaveAsync(job, ct);
        }
    }

    private async Task<bool> DispatchDirectAsync(NetworkEgressJob job, CancellationToken ct)
    {
        var peerProfile = await _peerRoutingProfileRepository.GetByIdAsync(job.DestinationPeerId, ct);
        if (peerProfile == null || peerProfile.Endpoints.Count == 0)
        {
            _logger.LogWarning("No endpoints found for peer {PeerId} in job {JobId}", job.DestinationPeerId.Value, job.JobId.Value);
            return false;
        }

        var endpoint = peerProfile.Endpoints.First().EndPoint;
        try
        {
            var request = new DeliverOpaqueMessageRequest
            {
                Payload = ByteString.CopyFrom(job.PayloadBytes.ToArray())
            };

            var callOptions = new CallOptions(cancellationToken: ct);
            var response = await _transportServiceClient.DeliverOpaqueMessageAsync(request, callOptions, ct);

            return response.Success;
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "gRPC error dispatching direct job {JobId} to {Host}:{Port}", job.JobId.Value, endpoint.Host, endpoint.Port);
            return false;
        }
    }

    private async Task<bool> DispatchRelayAsync(NetworkEgressJob job, CancellationToken ct)
    {
        var peerProfile = await _peerRoutingProfileRepository.GetByIdAsync(job.DestinationPeerId, ct);
        if (peerProfile == null || peerProfile.Relays.Count == 0)
        {
            _logger.LogWarning("No relays found for peer {PeerId} in job {JobId}", job.DestinationPeerId.Value, job.JobId.Value);
            return false;
        }

        // Use the first available relay
        var relayPeerId = peerProfile.Relays.First().RelayNetworkPeerId;
        var relayProfile = await _peerRoutingProfileRepository.GetByIdAsync(relayPeerId, ct);
        if (relayProfile == null || relayProfile.Endpoints.Count == 0)
        {
            _logger.LogWarning("No endpoints found for relay {RelayPeerId} in job {JobId}", relayPeerId.Value, job.JobId.Value);
            return false;
        }

        var relayEndpoint = relayProfile.Endpoints.First().EndPoint;

        try
        {
            if (job.PayloadType == PayloadType.Group)
            {
                var anonymousGroupRequest = AnonymousGroupRequest.Parser.ParseFrom(job.PayloadBytes.ToArray());

                // Anonymous egress - no auth headers for sealed-sender group messages
                var callOptions = new CallOptions(cancellationToken: ct);
                var response = await _anonymousGroupServiceClient.ProcessAnonymousGroupRequestAsync(anonymousGroupRequest, callOptions, ct);

                return response.Success;
            }
            else if (job.PayloadType == PayloadType.Opaque1to1)
            {
                var opaqueRequest = EnqueueOpaqueMessageRequest.Parser.ParseFrom(job.PayloadBytes.ToArray());
                
                // Authenticated egress - attach DeliveryCertificate headers for 1:1 messages
                var certificate = await _deliveryCertificateStore.GetCertificateAsync(
                    new ChatSelfId(job.DestinationPeerId.Value),
                    new ChatPeerId(relayPeerId.Value),
                    ct);

                if (certificate == null)
                {
                    _logger.LogWarning("No delivery certificate found for self {SelfId} to relay {RelayId} in job {JobId}", 
                        job.DestinationPeerId.Value, relayPeerId.Value, job.JobId.Value);
                    return false;
                }

                var headers = new Metadata();
                headers.Add("x-percolator-sender-public-identity-id", job.DestinationPeerId.Value.ToString());
                headers.Add("x-percolator-timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
                headers.Add("x-percolator-signature", Google.Protobuf.ByteString.CopyFrom(certificate.Signature.Span).ToBase64());

                var callOptions = new CallOptions(headers: headers, cancellationToken: ct);
                var response = await _relayServiceClient.EnqueueOpaqueMessageAsync(opaqueRequest, callOptions, ct);
                
                return response.Success;
            }
            
            return false;
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "gRPC error dispatching relay job {JobId} to relay {Host}:{Port}", job.JobId.Value, relayEndpoint.Host, relayEndpoint.Port);
            return false;
        }
    }

    private async Task<bool> DispatchAnyAsync(NetworkEgressJob job, CancellationToken ct)
    {
        // Try direct first, fall back to relay
        var directSuccess = await DispatchDirectAsync(job, ct);
        if (directSuccess)
        {
            return true;
        }

        return await DispatchRelayAsync(job, ct);
    }
}
