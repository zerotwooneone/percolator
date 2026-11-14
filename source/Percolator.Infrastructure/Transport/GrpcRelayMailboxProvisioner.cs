using Percolator.Application.Delivery.Ports;
using Percolator.Application.Ingress;
using Percolator.Application.Ports;

namespace Percolator.Infrastructure.Transport;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Infrastructure.Protos;

/// <summary>
/// Production <see cref="IRelayMailboxProvisioner"/> that talks to a relay host over the
/// authenticated relay gRPC surface: mailbox registration, signed pre-key bundle
/// publication (with routing-token owner association), and one-shot mailbox drains that
/// feed unwrapped envelopes into the inbound ingress pipeline.
/// </summary>
public sealed class GrpcRelayMailboxProvisioner : IRelayMailboxProvisioner
{
    private const uint SignedPreKeyId = 1;
    private static readonly TimeSpan EnvelopeExpiryWindow = TimeSpan.FromDays(7);

    private readonly IPeerGrpcChannelFactory _channelFactory;
    private readonly ILocalIdentityKeyStore _identityKeyStore;
    private readonly IPrivatePreKeyStore _preKeyStore;
    private readonly ICryptoEngine _cryptoEngine;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDateTimeProvider _timeProvider;

    public GrpcRelayMailboxProvisioner(
        IPeerGrpcChannelFactory channelFactory,
        ILocalIdentityKeyStore identityKeyStore,
        IPrivatePreKeyStore preKeyStore,
        ICryptoEngine cryptoEngine,
        IServiceScopeFactory scopeFactory,
        IDateTimeProvider timeProvider)
    {
        _channelFactory = channelFactory ?? throw new ArgumentNullException(nameof(channelFactory));
        _identityKeyStore = identityKeyStore ?? throw new ArgumentNullException(nameof(identityKeyStore));
        _preKeyStore = preKeyStore ?? throw new ArgumentNullException(nameof(preKeyStore));
        _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));
        // ISealedEnvelopeUnwrapper and IInboundIngressPipeline are per-node services
        // registered only by composition roots that know the node identity. They are
        // resolved lazily inside a per-drain scope in DrainMailboxOnceAsync so that hosts
        // without a local node identity can still construct this provisioner (required for
        // ValidateOnBuild composition checks).
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
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
        int newOneTimePreKeyCount = 25,
        CancellationToken ct = default)
    {
        if (newOneTimePreKeyCount < 0)
        {
            return DomainResult.Failure(new DomainError(
                "INVALID_OPK_COUNT", "One-time pre-key count cannot be negative."));
        }

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

        // Generate and persist the requested batch of fresh OPKs.
        var generated = new List<OneTimePreKeyMaterial>(newOneTimePreKeyCount);
        var opkProtos = new List<OneTimePreKeyProto>(newOneTimePreKeyCount);
        if (newOneTimePreKeyCount > 0)
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

        // Flat, drain-token-authenticated publish matching Signal's PUT /v2/keys.
        // No owner identity and no outer signature — the mailbox routing token is the
        // storage key and the drain token proves write authorization.
        var request = new PublishPreKeyBundleRequest
        {
            RoutingToken = ByteString.CopyFrom(tokens.RoutingToken.ToByteArray()),
            DrainToken = ByteString.CopyFrom(tokens.DrainToken.Span),
            DeviceId = deviceId.Value,
            IdentityKey = ByteString.CopyFrom(publicKey.Span),
            SignedPrekeyId = SignedPreKeyId,
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
        finally
        {
            foreach (var material in generated)
            {
                material.PrivateKey.Dispose();
            }
        }
    }

    public async Task<DomainResult<int>> DrainMailboxOnceAsync(
        Domain.Identities.PublicIdentityId ownerIdentityId,
        RelayEndpoint endpoint,
        MailboxTokenTriad tokens,
        CancellationToken ct = default)
    {
        var client = CreateClient(endpoint);
        DrainMailboxResponse drainResponse;
        try
        {
            drainResponse = await client.DrainMailboxAsync(
                new DrainMailboxRequest
                {
                    RoutingToken = ByteString.CopyFrom(tokens.RoutingToken.ToByteArray()),
                    DrainToken = ByteString.CopyFrom(tokens.DrainToken.Span)
                },
                cancellationToken: ct).ResponseAsync.ConfigureAwait(false);
        }
        catch (RpcException ex)
        {
            return DomainResult<int>.Failure(new DomainError(
                "RELAY_DRAIN_FAILED",
                $"Mailbox drain RPC failed: {ex.Status.Detail ?? ex.Message}"));
        }

        // One scope per drain: the per-node unwrapper/pipeline are scoped registrations
        // owned by the composition root, not resolvable from a singleton.
        using var drainScope = _scopeFactory.CreateScope();
        var unwrapper = drainScope.ServiceProvider.GetRequiredService<ISealedEnvelopeUnwrapper>();
        var ingress = drainScope.ServiceProvider.GetRequiredService<IInboundIngressPipeline>();

        var processed = 0;
        foreach (var queued in drainResponse.Envelopes)
        {
            try
            {
                var sealedEnv = SealedEnvelopeProto.Parser.ParseFrom(queued.EnvelopePayload);
                var decodeCheck = SealedWireFraming.Decode(sealedEnv.EncryptedPackage.Memory);
                if (decodeCheck.IsSuccess && decodeCheck.Value.SenderId == ownerIdentityId)
                {
                    // Skip self-echoes: envelopes we sent to ourselves through the mailbox.
                    continue;
                }

                var mailboxEnvelope = new MailboxEnvelope(
                    EnvelopeId.New(),
                    tokens.RoutingToken,
                    sealedEnv.EncryptedPackage.Memory,
                    _timeProvider.UtcNow,
                    _timeProvider.UtcNow + EnvelopeExpiryWindow);

                var unwrapResult = unwrapper.Unwrap(mailboxEnvelope);
                if (!unwrapResult.IsSuccess)
                {
                    continue;
                }

                var ingressResult = await ingress.ProcessInboundAsync(unwrapResult.Value!, ct)
                    .ConfigureAwait(false);
                if (ingressResult.IsSuccess)
                {
                    processed++;
                }
            }
            catch (Exception)
            {
                // Malformed/corrupt envelope — skip it; the rest of the drain continues.
            }
        }

        return DomainResult<int>.Success(processed);
    }

    public Task StopMailboxStreamAsync(
        Domain.Identities.PublicIdentityId ownerIdentityId,
        Domain.Identities.PublicIdentityId relayIdentityId,
        CancellationToken ct = default)
    {
        // This provisioner performs one-shot RPCs only and holds no live mailbox stream;
        // there is nothing to tear down.
        return Task.CompletedTask;
    }

    private AuthenticatedRelayService.AuthenticatedRelayServiceClient CreateClient(RelayEndpoint endpoint)
    {
        var channel = _channelFactory.GetOrCreateChannel(new Uri($"https://{endpoint.Value}"));
        return new AuthenticatedRelayService.AuthenticatedRelayServiceClient(channel);
    }
}
