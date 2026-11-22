using Percolator.Application.Delivery.Ports;
using Percolator.Domain.Delivery.Model;

namespace Percolator.Infrastructure.Transport;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Infrastructure.Protos;

/// <summary>
/// Production <see cref="IRelayMailboxProvisioner"/> that talks to a relay host over the
/// authenticated relay gRPC surface: mailbox registration and signed pre-key bundle
/// publication (with routing-token owner association).
/// </summary>
public sealed class GrpcRelayMailboxProvisioner : IRelayMailboxProvisioner
{
    // OPK key ids are allocated read-modify-write (max+1); concurrent publishes for
    // the same owner would collide on vault_onetime_prekeys' unique key, so the
    // allocate-generate-store stretch runs under this gate. Publishes are rare and
    // the critical section is short, so a single process-wide gate suffices.
    private readonly SemaphoreSlim _opkAllocationGate = new(1, 1);

    private readonly IPeerGrpcChannelFactory _channelFactory;
    private readonly ILocalIdentityKeyStore _identityKeyStore;
    private readonly IPrivatePreKeyStore _preKeyStore;
    private readonly ICryptoEngine _cryptoEngine;

    public GrpcRelayMailboxProvisioner(
        IPeerGrpcChannelFactory channelFactory,
        ILocalIdentityKeyStore identityKeyStore,
        IPrivatePreKeyStore preKeyStore,
        ICryptoEngine cryptoEngine)
    {
        _channelFactory = channelFactory ?? throw new ArgumentNullException(nameof(channelFactory));
        _identityKeyStore = identityKeyStore ?? throw new ArgumentNullException(nameof(identityKeyStore));
        _preKeyStore = preKeyStore ?? throw new ArgumentNullException(nameof(preKeyStore));
        _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));
    }

    public async Task<DomainResult> ProvisionMailboxAsync(
        Domain.Identities.PublicIdentityId ownerIdentityId,
        Domain.Identities.PublicIdentityId relayIdentityId,
        RelayEndpoint endpoint,
        MailboxTokenTriad tokens,
        CancellationToken ct = default)
    {
        var client = CreateClient(endpoint);
        var request = new RegisterMailboxRequest
        {
            RoutingToken = ByteString.CopyFrom(tokens.RoutingToken.ToByteArray()),
            AuthorizedDeliveryToken = ByteString.CopyFrom(tokens.DeliveryToken.Span),
            DrainToken = ByteString.CopyFrom(tokens.DrainToken.Span)
        };
        if (tokens.PreviousDrainToken != null)
        {
            request.ExistingDrainToken = ByteString.CopyFrom(tokens.PreviousDrainToken.Span);
        }

        try
        {
            var response = await client.RegisterMailboxAsync(request, cancellationToken: ct)
                .ResponseAsync.ConfigureAwait(false);
            return response.Success
                ? DomainResult.Success()
                : DomainResult.Failure(new DomainError(
                    "RELAY_REGISTER_REJECTED", "Relay rejected mailbox registration."));
        }
        catch (RpcException ex)
        {
            return DomainResult.Failure(new DomainError(
                "RELAY_REGISTER_FAILED",
                $"Mailbox registration RPC failed: {ex.Status.Detail ?? ex.Message}"));
        }
    }

    public async Task<DomainResult> PublishPreKeyBundleAsync(
        Domain.Identities.PublicIdentityId ownerIdentityId,
        DeviceId deviceId,
        RelayEndpoint endpoint,
        MailboxTokenTriad tokens,
        uint newOneTimePreKeyCount,
        CancellationToken ct = default)
    {

        var publicKey = await _identityKeyStore.GetIdentityPublicKeyAsync(ownerIdentityId, ct)
            .ConfigureAwait(false);
        var signedPreKey = await _preKeyStore.GetSignedPreKeyAsync(ownerIdentityId, deviceId, ct)
            .ConfigureAwait(false);

        if (publicKey == null || signedPreKey == null)
        {
            return DomainResult.Failure(new DomainError(
                "KEY_MATERIAL_MISSING",
                "Local identity or signed pre-key material is unavailable for publishing."));
        }

        // Generate and persist the requested batch of fresh OPKs under the
        // allocation gate — see _opkAllocationGate's comment. The outer finally
        // covers the whole method so a faulted generate/store can't leave private
        // key material undisposed.
        var generated = new List<OneTimePreKeyMaterial>((int)newOneTimePreKeyCount);
        try
        {
            var opkProtos = new List<OneTimePreKeyProto>((int)newOneTimePreKeyCount);
            if (newOneTimePreKeyCount > 0)
            {
                await _opkAllocationGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    uint startId = await _preKeyStore
                        .GetNextOneTimePreKeyIdAsync(ownerIdentityId, deviceId, ct)
                        .ConfigureAwait(false);

                    for (var i = 0; i < newOneTimePreKeyCount; i++)
                    {
                        var (opkPrivate, opkPublic) = _cryptoEngine.GenerateEphemeralKeyPair();
                        var keyId = startId + (uint)i;
                        generated.Add(new OneTimePreKeyMaterial(keyId, opkPrivate, opkPublic));
                        opkProtos.Add(new OneTimePreKeyProto
                        {
                            KeyId = keyId,
                            PublicKey = ByteString.CopyFrom(opkPublic.Span)
                        });
                    }

                    await _preKeyStore.StoreOneTimePreKeysAsync(ownerIdentityId, deviceId, generated, ct)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _opkAllocationGate.Release();
                }
            }

            // Flat, drain-token-authenticated publish matching Signal's PUT /v2/keys.
            // No owner identity and no outer signature — the mailbox routing token is the
            // storage key and the drain token proves write authorization.
            var request = new PublishPreKeyBundleRequest
            {
                RoutingToken = ByteString.CopyFrom(tokens.RoutingToken.ToByteArray()),
                DrainToken = ByteString.CopyFrom(tokens.DrainToken.Span),
                DeviceId = deviceId.Value,
                IdentityKey = ByteString.CopyFrom(publicKey.Span),
                SignedPrekeyId = signedPreKey.KeyId.Value,
                SignedPrekey = ByteString.CopyFrom(signedPreKey.PublicKey.Span),
                PrekeySignature = ByteString.CopyFrom(signedPreKey.Signature.Span)
            };
            request.OneTimePrekeys.AddRange(opkProtos);

            try
            {
                var client = CreateClient(endpoint);
                var response = await client.PublishPreKeyBundleAsync(
                    request,
                    cancellationToken: ct).ResponseAsync.ConfigureAwait(false);

                return response.Success
                    ? DomainResult.Success()
                    : DomainResult.Failure(new DomainError(
                        "RELAY_PUBLISH_REJECTED", "Relay rejected the published pre-key bundle."));
            }
            catch (RpcException ex)
            {
                return DomainResult.Failure(new DomainError(
                    "RELAY_PUBLISH_FAILED",
                    $"Pre-key bundle publish RPC failed: {ex.Status.Detail ?? ex.Message}"));
            }
        }
        finally
        {
            foreach (var material in generated)
            {
                material.PrivateKey.Dispose();
            }
        }
    }

    public async Task<DomainResult<RelayPreKeyStatus>> GetPreKeyStatusAsync(
        DeviceId deviceId,
        RelayEndpoint endpoint,
        MailboxTokenTriad tokens,
        CancellationToken ct = default)
    {
        var request = new GetPreKeyStatusRequest
        {
            RoutingToken = ByteString.CopyFrom(tokens.RoutingToken.ToByteArray()),
            DrainToken = ByteString.CopyFrom(tokens.DrainToken.Span),
            DeviceId = deviceId.Value
        };

        try
        {
            var client = CreateClient(endpoint);
            var response = await client.GetPreKeyStatusAsync(request, cancellationToken: ct)
                .ResponseAsync.ConfigureAwait(false);

            var status = response.Found
                ? new RelayPreKeyStatus(true, response.OneTimePrekeyCount, new SignedPreKeyId(response.SignedPrekeyId))
                : new RelayPreKeyStatus(false, 0, SignedPreKeyId.None);
            return DomainResult<RelayPreKeyStatus>.Success(status);
        }
        catch (RpcException ex)
        {
            return DomainResult<RelayPreKeyStatus>.Failure(new DomainError(
                "RELAY_PREKEY_STATUS_FAILED",
                $"Pre-key status RPC failed: {ex.Status.Detail ?? ex.Message}"));
        }
    }

    private AuthenticatedRelayService.AuthenticatedRelayServiceClient CreateClient(RelayEndpoint endpoint)
    {
        var channel = _channelFactory.GetOrCreateChannel(endpoint.ToUri());
        return new AuthenticatedRelayService.AuthenticatedRelayServiceClient(channel);
    }
}
