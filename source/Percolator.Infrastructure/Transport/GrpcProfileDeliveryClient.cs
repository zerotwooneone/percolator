namespace Percolator.Infrastructure.Transport;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Protos;
using Percolator.PluginSdk;

public sealed class GrpcProfileDeliveryClient : IProfileDeliveryClient
{
    private readonly IPeerGrpcChannelFactory? _channelFactory;
    private readonly UnauthenticatedDeliveryService.UnauthenticatedDeliveryServiceClient? _directClient;

    public GrpcProfileDeliveryClient(IPeerGrpcChannelFactory channelFactory)
    {
        _channelFactory = channelFactory ?? throw new ArgumentNullException(nameof(channelFactory));
    }

    public GrpcProfileDeliveryClient(UnauthenticatedDeliveryService.UnauthenticatedDeliveryServiceClient directClient)
    {
        _directClient = directClient ?? throw new ArgumentNullException(nameof(directClient));
    }

    public async ValueTask<DomainResult<ProfileCiphertextPackage>> FetchProfileAsync(
        Uri? endpointUri,
        BlindedProfileLocator locator,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(locator);

        try
        {
            var client = GetClient(endpointUri);
            if (client == null)
            {
                return DomainResult<ProfileCiphertextPackage>.Failure(new DomainError(
                    "ENDPOINT_REQUIRED",
                    "Endpoint URI is required when no direct client is configured."));
            }

            var request = new ProfileFetchRequestProto
            {
                BlindedLocator = ByteString.CopyFrom(locator.Span)
            };

            var response = await client.GetProfileAsync(request, cancellationToken: ct);
            if (!response.Found)
            {
                return DomainResult<ProfileCiphertextPackage>.Failure(new DomainError(
                    "PROFILE_NOT_FOUND",
                    "Profile was not found on the remote endpoint."));
            }

            var package = new ProfileCiphertextPackage(
                response.Nonce.ToByteArray(),
                response.Ciphertext.ToByteArray());

            return DomainResult<ProfileCiphertextPackage>.Success(package);
        }
        catch (RpcException ex)
        {
            return DomainResult<ProfileCiphertextPackage>.Failure(new DomainError(
                "PROFILE_RPC_ERROR",
                ex.Status.Detail ?? ex.Message));
        }
        catch (Exception ex)
        {
            return DomainResult<ProfileCiphertextPackage>.Failure(new DomainError(
                "PROFILE_FETCH_FAILED",
                ex.Message));
        }
    }

    public async ValueTask<DomainResult> UploadProfileAsync(
        Uri? endpointUri,
        BlindedProfileLocator locator,
        ProfileWriteToken writeToken,
        ProfileCiphertextPackage package,
        uint revision,
        DateTimeOffset expiresAtUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(writeToken);

        try
        {
            var client = GetClient(endpointUri);
            if (client == null)
            {
                return DomainResult.Failure(new DomainError(
                    "ENDPOINT_REQUIRED",
                    "Endpoint URI is required when no direct client is configured."));
            }

            var request = new ProfileUploadRequestProto
            {
                BlindedLocator = ByteString.CopyFrom(locator.Span),
                WriteToken = ByteString.CopyFrom(writeToken.Span),
                Revision = revision,
                Nonce = ByteString.CopyFrom(package.Nonce),
                Ciphertext = ByteString.CopyFrom(package.Ciphertext),
                ExpiresAtUtc = Timestamp.FromDateTimeOffset(expiresAtUtc)
            };

            var response = await client.UploadProfileAsync(request, cancellationToken: ct);
            if (!response.Success)
            {
                return DomainResult.Failure(new DomainError(
                    "PROFILE_UPLOAD_REJECTED",
                    response.ErrorMessage ?? "Profile upload was rejected by remote endpoint."));
            }

            return DomainResult.Success();
        }
        catch (RpcException ex)
        {
            return DomainResult.Failure(new DomainError(
                "PROFILE_RPC_ERROR",
                ex.Status.Detail ?? ex.Message));
        }
        catch (Exception ex)
        {
            return DomainResult.Failure(new DomainError(
                "PROFILE_UPLOAD_FAILED",
                ex.Message));
        }
    }

    public async ValueTask<DomainResult> DeleteProfileAsync(
        Uri? endpointUri,
        BlindedProfileLocator locator,
        ProfileWriteToken writeToken,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(writeToken);

        try
        {
            var client = GetClient(endpointUri);
            if (client == null)
            {
                return DomainResult.Failure(new DomainError(
                    "ENDPOINT_REQUIRED",
                    "Endpoint URI is required when no direct client is configured."));
            }

            var request = new ProfileDeleteRequestProto
            {
                BlindedLocator = ByteString.CopyFrom(locator.Span),
                WriteToken = ByteString.CopyFrom(writeToken.Span)
            };

            var response = await client.DeleteProfileAsync(request, cancellationToken: ct);
            if (!response.Success)
            {
                return DomainResult.Failure(new DomainError(
                    "PROFILE_DELETE_REJECTED",
                    response.ErrorMessage ?? "Profile deletion was rejected by remote endpoint."));
            }

            return DomainResult.Success();
        }
        catch (RpcException ex)
        {
            return DomainResult.Failure(new DomainError(
                "PROFILE_RPC_ERROR",
                ex.Status.Detail ?? ex.Message));
        }
        catch (Exception ex)
        {
            return DomainResult.Failure(new DomainError(
                "PROFILE_DELETE_FAILED",
                ex.Message));
        }
    }

    public async Task<IReadOnlyDictionary<BlindedProfileLocator, DomainResult<ProfileCiphertextPackage>>> BatchFetchProfilesAsync(
        IEnumerable<(Uri? Endpoint, BlindedProfileLocator Locator)> targets,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targets);

        var tasks = targets.Select(async target =>
        {
            try
            {
                var result = await FetchProfileAsync(target.Endpoint, target.Locator, ct).ConfigureAwait(false);
                return (target.Locator, Result: result);
            }
            catch (Exception ex)
            {
                return (target.Locator, Result: DomainResult<ProfileCiphertextPackage>.Failure(new DomainError(
                    "BATCH_FETCH_FAILED",
                    ex.Message)));
            }
        });

        var completed = await Task.WhenAll(tasks).ConfigureAwait(false);
        var results = new Dictionary<BlindedProfileLocator, DomainResult<ProfileCiphertextPackage>>();
        foreach (var (locator, result) in completed)
        {
            results[locator] = result;
        }

        return results;
    }

    private UnauthenticatedDeliveryService.UnauthenticatedDeliveryServiceClient? GetClient(Uri? endpointUri)
    {
        if (_directClient != null)
        {
            return _directClient;
        }

        if (endpointUri == null || _channelFactory == null)
        {
            return null;
        }

        var channel = _channelFactory.GetOrCreateChannel(endpointUri);
        return new UnauthenticatedDeliveryService.UnauthenticatedDeliveryServiceClient(channel);
    }
}
