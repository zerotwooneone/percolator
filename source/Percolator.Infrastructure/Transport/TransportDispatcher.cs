namespace Percolator.Infrastructure.Transport;

using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Percolator.Application2.Delivery;
using Percolator.Application2.Delivery.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Security.Ports;
using Percolator.Infrastructure.Protos;

public interface ITransportClientInvoker
{
    Task<DeliveryAckProto> DeliverDirectAsync(Uri endpoint, SealedEnvelopeProto request, CancellationToken ct);
    Task<EnqueueAckProto> EnqueueMailboxAsync(Uri endpoint, SealedEnvelopeProto request, CancellationToken ct);
    Task<DispatchGroupMessageResponse> DispatchGroupMessageAsync(Uri endpoint, DispatchGroupMessageRequest request, CancellationToken ct);
}

public sealed class GrpcTransportClientInvoker : ITransportClientInvoker
{
    private readonly IPeerGrpcChannelFactory _channelFactory;

    public GrpcTransportClientInvoker(IPeerGrpcChannelFactory channelFactory)
    {
        _channelFactory = channelFactory ?? throw new ArgumentNullException(nameof(channelFactory));
    }

    public async Task<DeliveryAckProto> DeliverDirectAsync(Uri endpoint, SealedEnvelopeProto request, CancellationToken ct)
    {
        var channel = _channelFactory.GetOrCreateChannel(endpoint);
        var client = new UnauthenticatedDeliveryService.UnauthenticatedDeliveryServiceClient(channel);
        return await client.DeliverDirectAsync(request, cancellationToken: ct);
    }

    public async Task<EnqueueAckProto> EnqueueMailboxAsync(Uri endpoint, SealedEnvelopeProto request, CancellationToken ct)
    {
        var channel = _channelFactory.GetOrCreateChannel(endpoint);
        var client = new UnauthenticatedDeliveryService.UnauthenticatedDeliveryServiceClient(channel);
        return await client.EnqueueMailboxAsync(request, cancellationToken: ct);
    }

    public async Task<DispatchGroupMessageResponse> DispatchGroupMessageAsync(Uri endpoint, DispatchGroupMessageRequest request, CancellationToken ct)
    {
        var channel = _channelFactory.GetOrCreateChannel(endpoint);
        var client = new AnonymousGroupRelayService.AnonymousGroupRelayServiceClient(channel);
        return await client.DispatchGroupMessageAsync(request, cancellationToken: ct);
    }
}

public sealed class TransportDispatcher : ITransportDispatcher
{
    private readonly ITransportClientInvoker _invoker;
    private readonly IZkProofEngine? _zkProofEngine;
    private readonly IGroupCredentialsRepository? _groupCredentialsRepository;

    public TransportDispatcher(
        ITransportClientInvoker invoker,
        IZkProofEngine? zkProofEngine = null,
        IGroupCredentialsRepository? groupCredentialsRepository = null)
    {
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _zkProofEngine = zkProofEngine;
        _groupCredentialsRepository = groupCredentialsRepository;
    }

    public async ValueTask<DomainResult> SendAsync(OutboxJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        var route = job.Route;
        try
        {
            switch (route.Type)
            {
                case DeliveryRouteType.DirectP2P:
                {
                    if (route.DirectEndpoint == null)
                    {
                        return DomainResult.Failure(new DomainError("MISSING_DIRECT_ENDPOINT", "Direct endpoint is required for DirectP2P route."));
                    }

                    var envelopeProto = new SealedEnvelopeProto
                    {
                        RecipientRoutingToken = route.TargetToken.HasValue
                            ? ByteString.CopyFrom(route.TargetToken.Value.ToByteArray())
                            : ByteString.Empty,
                        DeliveryToken = route.TargetDeliveryToken != null
                            ? ByteString.CopyFrom(route.TargetDeliveryToken.Span)
                            : ByteString.Empty,
                        EncryptedPackage = ByteString.CopyFrom(job.Payload.Span),
                        ExpiresAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(7))
                    };

                    var ack = await _invoker.DeliverDirectAsync(route.DirectEndpoint, envelopeProto, ct);
                    if (!ack.Success)
                    {
                        return DomainResult.Failure(new DomainError("DELIVERY_FAILED", ack.ErrorMessage ?? "Direct delivery rejected."));
                    }

                    return DomainResult.Success();
                }

                case DeliveryRouteType.RelayedOneToOne:
                {
                    if (route.RelayEndpoint == null)
                    {
                        return DomainResult.Failure(new DomainError("MISSING_RELAY_ENDPOINT", "Relay endpoint is required for RelayedOneToOne route."));
                    }

                    var endpointStr = route.RelayEndpoint.Value;
                    var relayUri = endpointStr.Contains("://")
                        ? new Uri(endpointStr)
                        : new Uri($"https://{endpointStr}");

                    var envelopeProto = new SealedEnvelopeProto
                    {
                        RecipientRoutingToken = route.TargetToken.HasValue
                            ? ByteString.CopyFrom(route.TargetToken.Value.ToByteArray())
                            : ByteString.Empty,
                        DeliveryToken = route.TargetDeliveryToken != null
                            ? ByteString.CopyFrom(route.TargetDeliveryToken.Span)
                            : ByteString.Empty,
                        EncryptedPackage = ByteString.CopyFrom(job.Payload.Span),
                        ExpiresAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(7))
                    };

                    var ack = await _invoker.EnqueueMailboxAsync(relayUri, envelopeProto, ct);
                    if (!ack.Accepted)
                    {
                        return DomainResult.Failure(new DomainError("RELAY_ENQUEUE_REJECTED", "Relay rejected mailbox enqueue."));
                    }

                    return DomainResult.Success();
                }

                case DeliveryRouteType.RelayedGroup:
                {
                    if (route.RelayEndpoint == null)
                    {
                        return DomainResult.Failure(new DomainError("MISSING_RELAY_ENDPOINT", "Relay endpoint is required for RelayedGroup route."));
                    }

                    var endpointStr = route.RelayEndpoint.Value;
                    var relayUri = endpointStr.Contains("://")
                        ? new Uri(endpointStr)
                        : new Uri($"https://{endpointStr}");

                    byte[]? masterKeyBytes = null;
                    byte[]? authMacBytes = null;

                    if (_zkProofEngine != null && _groupCredentialsRepository != null)
                    {
                        var creds = await _groupCredentialsRepository.GetAsync(job.ChannelId, ct);
                        if (creds != null)
                        {
                            masterKeyBytes = creds.MasterKey.Span.ToArray();
                            authMacBytes = creds.AuthCredentialMac.Span.ToArray();
                        }
                    }

                    byte[] zkProof = Array.Empty<byte>();
                    uint epoch = 0;
                    if (_zkProofEngine != null)
                    {
                        Span<byte> challengeHash = stackalloc byte[32];
                        RelayGroupLedger.ComputeDispatchChallenge(job.Payload.Span, challengeHash);

                        var proofBytes = _zkProofEngine.GenerateGroupPresentation(
                            epoch,
                            challengeHash,
                            masterKeyBytes ?? ReadOnlySpan<byte>.Empty,
                            authMacBytes ?? ReadOnlySpan<byte>.Empty);
                        zkProof = proofBytes.Span.ToArray();

                        if (masterKeyBytes != null)
                        {
                            CryptographicOperations.ZeroMemory(masterKeyBytes);
                        }
                    }

                    var groupRequest = new DispatchGroupMessageRequest
                    {
                        ConversationId = ByteString.CopyFrom(job.ChannelId.ToByteArray()),
                        Epoch = epoch,
                        ZkPresentationProof = ByteString.CopyFrom(zkProof),
                        SenderKeyIteration = 0,
                        Ciphertext = ByteString.CopyFrom(job.Payload.Span),
                        AuthorSignature = ByteString.Empty
                    };

                    var response = await _invoker.DispatchGroupMessageAsync(relayUri, groupRequest, ct);
                    if (!response.Accepted)
                    {
                        return DomainResult.Failure(new DomainError("GROUP_DISPATCH_REJECTED", "Group relay rejected message dispatch."));
                    }

                    return DomainResult.Success();
                }

                default:
                    return DomainResult.Failure(new DomainError("UNSUPPORTED_ROUTE", $"Unsupported route type: {route.Type}"));
            }
        }
        catch (RpcException rpcEx)
        {
            return DomainResult.Failure(new DomainError("RPC_ERROR", rpcEx.Status.Detail ?? rpcEx.Message));
        }
        catch (Exception ex)
        {
            return DomainResult.Failure(new DomainError("TRANSPORT_ERROR", ex.Message));
        }
    }
}
