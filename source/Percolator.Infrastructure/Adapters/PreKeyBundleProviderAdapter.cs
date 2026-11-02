namespace Percolator.Infrastructure.Adapters;

using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Percolator.Application2.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Transport;
using Percolator.PluginSdk;

public sealed class PreKeyBundleProviderAdapter : IPreKeyBundleProvider
{
    private readonly IRelayPreKeyRepository? _preKeyRepository;
    private readonly IPeerGrpcChannelFactory? _channelFactory;
    private readonly IPeerMailboxRoutingRepository? _routingRepository;

    public PreKeyBundleProviderAdapter(
        IRelayPreKeyRepository? preKeyRepository = null,
        IPeerGrpcChannelFactory? channelFactory = null,
        IPeerMailboxRoutingRepository? routingRepository = null)
    {
        _preKeyRepository = preKeyRepository;
        _channelFactory = channelFactory;
        _routingRepository = routingRepository;
    }

    public async Task<DomainResult<PreKeyBundle>> FetchConsumableBundleAsync(
        PublicIdentityId peerId,
        DeviceId? deviceId = null,
        RelayEndpoint? relayEndpoint = null,
        CancellationToken ct = default)
    {
        var targetDevice = deviceId ?? DeviceId.Primary;

        // 1. Check local prekey repository if configured
        if (_preKeyRepository != null)
        {
            var hostedBundle = await _preKeyRepository.GetAsync(peerId, targetDevice, ct).ConfigureAwait(false);
            if (hostedBundle != null)
            {
                var consumeResult = hostedBundle.ConsumeBundle();
                if (consumeResult.IsSuccess)
                {
                    await _preKeyRepository.SaveAsync(hostedBundle, ct).ConfigureAwait(false);
                }
                return consumeResult;
            }
        }

        // 2. If remote relay endpoint is specified and channel factory & routing repository are configured
        if (relayEndpoint != null && _channelFactory != null && _routingRepository != null)
        {
            try
            {
                var routingCapability = await _routingRepository.GetByPeerIdAsync(peerId, ct).ConfigureAwait(false);
                if (routingCapability != null)
                {
                    var uri = ToUri(relayEndpoint);
                    var channel = _channelFactory.GetOrCreateChannel(uri);
                    var client = new UnauthenticatedDeliveryService.UnauthenticatedDeliveryServiceClient(channel);

                    var request = new FetchPreKeyBundleRequest
                    {
                        RecipientRoutingToken = ByteString.CopyFrom(routingCapability.RoutingToken.ToByteArray()),
                        DeliveryToken = ByteString.CopyFrom(routingCapability.DeliveryToken.Span)
                    };

                    var response = await client.FetchPreKeyBundleAsync(request, cancellationToken: ct);

                    var identityKey = IdentityKey.FromSpan(response.IdentityKey.Span);
                    var signedPreKey = DhPublicKey.FromSpan(response.SignedPrekey.Span);
                    var prekeySig = DeviceLinkProof.FromSpan(response.PrekeySignature.Span);
                    DhPublicKey? otpk = response.OneTimePrekey != null && !response.OneTimePrekey.IsEmpty
                        ? DhPublicKey.FromSpan(response.OneTimePrekey.Span)
                        : null;

                    var bundle = new PreKeyBundle(
                        peerId,
                        targetDevice,
                        identityKey,
                        signedPreKey,
                        prekeySig,
                        otpk,
                        response.OneTimePrekeyId);

                    return DomainResult<PreKeyBundle>.Success(bundle);
                }
            }
            catch (RpcException ex)
            {
                return DomainResult<PreKeyBundle>.Failure(new DomainError(
                    "PREKEY_BUNDLE_RPC_ERROR",
                    $"Remote relay RPC error: {ex.Status.Detail ?? ex.Message}"));
            }
            catch (Exception ex)
            {
                return DomainResult<PreKeyBundle>.Failure(new DomainError(
                    "PREKEY_BUNDLE_FETCH_FAILED",
                    $"Failed to fetch remote prekey bundle: {ex.Message}"));
            }
        }

        return DomainResult<PreKeyBundle>.Failure(new DomainError(
            "PREKEY_BUNDLE_NOT_FOUND",
            $"No prekey bundle hosted for peer '{peerId}'."));
    }

    private static Uri ToUri(RelayEndpoint endpoint)
    {
        if (endpoint.Value.Contains("://") && Uri.TryCreate(endpoint.Value, UriKind.Absolute, out var uri))
        {
            return uri;
        }

        return new Uri($"https://{endpoint.Value}");
    }
}
