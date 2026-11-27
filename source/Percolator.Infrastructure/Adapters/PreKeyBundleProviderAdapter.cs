namespace Percolator.Infrastructure.Adapters;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Percolator.Application.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Transport;
using Percolator.PluginSdk.Profiles;

public sealed class PreKeyBundleProviderAdapter : IPreKeyBundleProvider
{
    private readonly IRelayPreKeyRepository? _preKeyRepository;
    private readonly IPeerGrpcChannelFactory _channelFactory;
    private readonly IPeerMailboxRoutingRepository _routingRepository;
    private readonly IRelayDirectory _relayDirectory;

    public PreKeyBundleProviderAdapter(
        IPeerMailboxRoutingRepository routingRepository,
        IPeerGrpcChannelFactory channelFactory,
        IRelayDirectory relayDirectory,
        IRelayPreKeyRepository? preKeyRepository = null)
    {
        _routingRepository = routingRepository ?? throw new ArgumentNullException(nameof(routingRepository));
        _channelFactory = channelFactory ?? throw new ArgumentNullException(nameof(channelFactory));
        _relayDirectory = relayDirectory ?? throw new ArgumentNullException(nameof(relayDirectory));
        _preKeyRepository = preKeyRepository;
    }

    public async Task<DomainResult<PreKeyBundle>> FetchAuthenticatedAsync(
        PublicIdentityId requesterId,
        RelayEndpoint relayEndpoint,
        BlindedRoutingToken targetRoutingToken,
        DeviceId? targetDeviceId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(relayEndpoint);
        var targetDevice = targetDeviceId ?? DeviceId.Primary;

        var entries = await _relayDirectory.ListAsync(ct).ConfigureAwait(false);
        var entry = entries.FirstOrDefault(e => e.Endpoint == relayEndpoint && e.LinkedOwnerIdentityIds.Contains(requesterId));
        if (entry == null)
        {
            return DomainResult<PreKeyBundle>.Failure(new DomainError(
                "EXCHANGE_NOT_LINKED", $"Requester {requesterId} has no linked mailbox on relay {relayEndpoint}."));
        }

        var tokensResult = await _relayDirectory.GetMailboxTokensAsync(requesterId, entry.RelayIdentityId, ct).ConfigureAwait(false);
        if (tokensResult.IsFailure)
        {
            return DomainResult<PreKeyBundle>.Failure(tokensResult.Error);
        }

        var tokens = tokensResult.Value;
        var request = new FetchPreKeyBundleRequestProto
        {
            RoutingToken = ByteString.CopyFrom(tokens.RoutingToken.ToByteArray()),
            DrainToken = ByteString.CopyFrom(tokens.DrainToken.Span),
            TargetRoutingToken = ByteString.CopyFrom(targetRoutingToken.ToByteArray()),
            DeviceId = targetDevice.Value
        };

        try
        {
            var uri = ToUri(relayEndpoint);
            var channel = _channelFactory.GetOrCreateChannel(uri, ChannelPoolKind.Authenticated);
            var client = new AuthenticatedRelayService.AuthenticatedRelayServiceClient(channel);
            var response = await client.FetchPreKeyBundleAsync(request, cancellationToken: ct);

            var identityKey = IdentityKey.FromSpan(response.IdentityKey.Span);
            var signedPreKey = DhPublicKey.FromSpan(response.SignedPrekey.Span);
            var prekeySig = DeviceLinkProof.FromSpan(response.PrekeySignature.Span);
            DhPublicKey? otpk = response.OneTimePrekey != null && !response.OneTimePrekey.IsEmpty
                ? DhPublicKey.FromSpan(response.OneTimePrekey.Span)
                : null;

            var bundle = new PreKeyBundle(
                targetDevice,
                identityKey,
                signedPreKey,
                prekeySig,
                response.SignedPrekeyId == 0 ? SignedPreKeyId.First : new SignedPreKeyId(response.SignedPrekeyId),
                otpk,
                response.OneTimePrekeyId);

            return DomainResult<PreKeyBundle>.Success(bundle);
        }
        catch (RpcException ex)
        {
            return DomainResult<PreKeyBundle>.Failure(new DomainError(
                ex.StatusCode.ToString(),
                $"Remote relay RPC error: {ex.Status.Detail ?? ex.Message}"));
        }
        catch (Exception ex)
        {
            return DomainResult<PreKeyBundle>.Failure(new DomainError(
                "PREKEY_BUNDLE_FETCH_FAILED",
                $"Failed to fetch remote prekey bundle: {ex.Message}"));
        }
    }

    public async Task<DomainResult<PreKeyBundle>> FetchConsumableBundleAsync(
        PublicIdentityId requesterId,
        PublicIdentityId peerId,
        DeviceId? deviceId = null,
        RelayEndpoint? relayEndpoint = null,
        CancellationToken ct = default)
    {
        if (!requesterId.IsValid)
        {
            return DomainResult<PreKeyBundle>.Failure(new DomainError(
                "INVALID_REQUESTER_ID",
                "Requester identity is invalid."));
        }

        var targetDevice = deviceId ?? DeviceId.Primary;

        PeerMailboxRoutingCapability? routingCapability = null;
        if (relayEndpoint != null)
        {
            var routes = await _routingRepository.GetRoutesForPeerAsync(peerId, ct).ConfigureAwait(false);
            routingCapability = routes.FirstOrDefault(r => r.RelayEndpoint == relayEndpoint);
        }

        routingCapability ??= await _routingRepository.GetByPeerIdAsync(peerId, ct).ConfigureAwait(false);
        if (routingCapability == null)
        {
            return DomainResult<PreKeyBundle>.Failure(new DomainError(
                "PREKEY_BUNDLE_NOT_FOUND",
                $"No prekey bundle hosted for peer '{peerId}'."));
        }

        if (_preKeyRepository != null)
        {
            var consumed = await _preKeyRepository.ConsumePreKeyBundleAsync(routingCapability.RoutingToken, targetDevice, ct).ConfigureAwait(false);
            if (consumed != null)
            {
                return DomainResult<PreKeyBundle>.Success(consumed);
            }
        }

        var effectiveEndpoint = relayEndpoint ?? routingCapability.RelayEndpoint;
        if (effectiveEndpoint != null)
        {
            return await FetchAuthenticatedAsync(requesterId, effectiveEndpoint, routingCapability.RoutingToken, targetDevice, ct).ConfigureAwait(false);
        }

        return DomainResult<PreKeyBundle>.Failure(new DomainError(
            "PREKEY_BUNDLE_NOT_FOUND",
            $"No prekey bundle hosted for peer '{peerId}'."));
    }

    private static Uri ToUri(RelayEndpoint endpoint) => endpoint.ToUri();
}
