using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Application2.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Security;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Transport;
using Percolator.PluginSdk;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Features.RelayedChat;

internal sealed class RelayProvisioningCoordinator
{
    private readonly ISimulatedWorldStateService _worldState;
    private readonly ISimulatorStateBus _stateBus;
    private readonly IStatusToastService _toastService;
    private readonly ISimulatorLogSink _logSink;
    private readonly SignalCryptoEngine _crypto = new();

    public RelayProvisioningCoordinator(
        ISimulatedWorldStateService worldState,
        ISimulatorStateBus stateBus,
        IStatusToastService toastService,
        ISimulatorLogSink logSink)
    {
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _stateBus = stateBus ?? throw new ArgumentNullException(nameof(stateBus));
        _toastService = toastService ?? throw new ArgumentNullException(nameof(toastService));
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
    }

    public async Task<bool> RegisterMailboxAsync(
        ISimulatedPersonaHost persona,
        ISimulatedRelayHost relay,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(persona);
        ArgumentNullException.ThrowIfNull(relay);

        try
        {
            using (var scope = persona.Services.CreateScope())
            {
                var channelFactory = scope.ServiceProvider.GetRequiredService<IPeerGrpcChannelFactory>();
                var relayUri = new Uri($"https://127.0.0.1:{relay.BoundPort}");
                var channel = channelFactory.GetOrCreateChannel(relayUri);
                var client = new AuthenticatedRelayService.AuthenticatedRelayServiceClient(channel);

                var regReq = new RegisterMailboxRequest
                {
                    RoutingToken = ByteString.CopyFrom(persona.MailboxRoutingToken.ToByteArray()),
                    AuthorizedDeliveryToken = ByteString.CopyFrom(persona.MailboxDeliveryToken.Span),
                    DrainToken = ByteString.CopyFrom(persona.MailboxDrainToken.Span)
                };

                var regResp = await client.RegisterMailboxAsync(regReq, cancellationToken: ct);
                if (!regResp.Success)
                {
                    _toastService.ShowToast("Registration Failed", "Relay rejected mailbox registration.", ToastSeverity.Error);
                    return false;
                }
            }

            await relay.MailboxStore.AssociateOwnerAsync(
                persona.MailboxRoutingToken.ToByteArray(),
                persona.PublicIdentityId,
                DeviceId.Primary,
                ct);

            persona.HomeRelay = relay;
            await persona.PublishProfileAsync(ct);

            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Information,
                persona.DisplayName,
                persona.Id,
                "RelayProvisioning",
                $"Mailbox registered on relay '{relay.DisplayName}' (Port: {relay.BoundPort})."));

            return true;
        }
        catch (Exception ex)
        {
            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Error,
                persona.DisplayName,
                persona.Id,
                "RelayProvisioning",
                $"Failed to register mailbox: {ex.Message}",
                ex.ToString()));
            return false;
        }
    }

    public async Task<bool> PublishPreKeysAsync(
        ISimulatedPersonaHost persona,
        ISimulatedRelayHost relay,
        int count = 25,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(persona);
        ArgumentNullException.ThrowIfNull(relay);

        try
        {
            var existingOpks = await relay.QueryService.GetHostedOneTimePreKeysAsync(persona.PublicIdentityId, DeviceId.Primary, ct);
            uint startId = persona.AllocateNextOpkIdRange(count);

            // 1. Generate OTPKs
            var otpkList = new List<(uint KeyId, DhPublicKey Key)>();
            var otpkPrivList = new List<(uint KeyId, EphemeralPrivateKey Key)>();

            for (uint i = startId; i < startId + (uint)count; i++)
            {
                var (priv, pub) = _crypto.GenerateEphemeralKeyPair();
                otpkList.Add((i, pub));
                otpkPrivList.Add((i, priv));
            }

            // 2. Store private keys in persona's IPrivatePreKeyStore
            using (var scope = persona.Services.CreateScope())
            {
                var preKeyStore = scope.ServiceProvider.GetRequiredService<IPrivatePreKeyStore>();
                await preKeyStore.StoreOneTimePreKeysPrivateAsync(persona.PublicIdentityId, DeviceId.Primary, otpkPrivList, ct);
            }

            // 3. Ensure mailbox is registered via gRPC
            await RegisterMailboxAsync(persona, relay, ct);

            // 4. Construct PreKeyBundlePayloadProto with newly generated OPKs
            var payload = new PreKeyBundlePayloadProto
            {
                OwnerIdentityId = ByteString.CopyFrom(persona.PublicIdentityId.ToByteArray()),
                DeviceId = DeviceId.Primary.Value,
                IdentityKey = ByteString.CopyFrom(persona.IdentityPublicKey.Span),
                SignedPrekeyId = 1,
                SignedPrekey = ByteString.CopyFrom(persona.SignedPreKeyPublic.Span),
                PrekeySignature = ByteString.CopyFrom(persona.SignedPreKeySignature.Span),
                PublishedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
            };

            if (existingOpks.Count > 0)
            {
                foreach (var opk in existingOpks)
                {
                    payload.OneTimePrekeys.Add(new OneTimePreKeyProto
                    {
                        KeyId = opk.KeyId,
                        PublicKey = ByteString.CopyFrom(opk.PublicKey)
                    });
                }
            }

            foreach (var (keyId, pubKey) in otpkList)
            {
                payload.OneTimePrekeys.Add(new OneTimePreKeyProto
                {
                    KeyId = keyId,
                    PublicKey = ByteString.CopyFrom(pubKey.Span)
                });
            }

            byte[] payloadBytes = payload.ToByteArray();
            byte[] signature = _crypto.SignEd25519(persona.IdentityPrivateKey.Span, payloadBytes);

            var publishRequest = new PublishPreKeyBundleRequest
            {
                PayloadBytes = ByteString.CopyFrom(payloadBytes),
                Signature = ByteString.CopyFrom(signature)
            };

            // 5. Invoke AuthenticatedRelayService.PublishPreKeyBundle over gRPC
            using (var scope = persona.Services.CreateScope())
            {
                var channelFactory = scope.ServiceProvider.GetRequiredService<IPeerGrpcChannelFactory>();
                var relayUri = new Uri($"https://127.0.0.1:{relay.BoundPort}");
                var channel = channelFactory.GetOrCreateChannel(relayUri);
                var client = new AuthenticatedRelayService.AuthenticatedRelayServiceClient(channel);

                var publishResponse = await client.PublishPreKeyBundleAsync(publishRequest, cancellationToken: ct);
                if (!publishResponse.Success)
                {
                    _toastService.ShowToast("Publish Error", "Relay rejected PreKey bundle publication.", ToastSeverity.Error);
                    return false;
                }
            }

            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Information,
                persona.DisplayName,
                persona.Id,
                "RelayProvisioning",
                $"Published {count} OPKs to relay '{relay.DisplayName}' (Available: {payload.OneTimePrekeys.Count})."));

            return true;
        }
        catch (Exception ex)
        {
            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Error,
                persona.DisplayName,
                persona.Id,
                "RelayProvisioning",
                $"Failed to publish prekeys: {ex.Message}",
                ex.ToString()));
            return false;
        }
    }

    public async Task<int> BulkRegisterMailboxesAsync(
        IEnumerable<ISimulatedPersonaHost> personas,
        ISimulatedRelayHost relay,
        CancellationToken ct = default)
    {
        int count = 0;
        foreach (var p in personas)
        {
            if (await RegisterMailboxAsync(p, relay, ct))
            {
                count++;
            }
        }
        return count;
    }

    public async Task<int> BulkPublishPreKeysAsync(
        IEnumerable<ISimulatedPersonaHost> personas,
        ISimulatedRelayHost relay,
        int opkCount = 25,
        CancellationToken ct = default)
    {
        int count = 0;
        foreach (var p in personas)
        {
            if (await PublishPreKeysAsync(p, relay, opkCount, ct))
            {
                count++;
            }
        }
        return count;
    }

    public async Task<bool> InitiateRelayedHandshakeAsync(
        SimulatedNodeId initiatorNodeId,
        SimulatedNodeId targetNodeId,
        SimulatedNodeId? relayNodeId = null,
        CancellationToken ct = default)
    {
        var initiator = _worldState.GetPersonaHost(initiatorNodeId);
        var target = _worldState.GetPersonaHost(targetNodeId);

        if (initiator == null || target == null)
        {
            _toastService.ShowToast("Handshake Failed", "Both initiator and target must be active.", ToastSeverity.Error);
            return false;
        }

        ISimulatedRelayHost? relay = null;
        if (relayNodeId.HasValue)
        {
            relay = _worldState.GetRelayHost(relayNodeId.Value);
        }
        else if (target.HomeRelay != null)
        {
            relay = target.HomeRelay;
        }
        else
        {
            relay = _worldState.GetAllRelayHosts().FirstOrDefault();
        }

        if (relay == null)
        {
            _toastService.ShowToast("Relay Required", "No active relay found. Add a Relay first.", ToastSeverity.Warning);
            return false;
        }

        try
        {
            // 1. Ensure target has mailbox registered and prekeys published on this relay
            if (target.HomeRelay == null || !await relay.QueryService.HasActivePreKeyAsync(target.PublicIdentityId, ct))
            {
                await PublishPreKeysAsync(target, relay, 25, ct);
            }

            // 2. Ensure initiator also has mailbox registered on this relay
            if (initiator.HomeRelay == null)
            {
                await RegisterMailboxAsync(initiator, relay, ct);
            }

            // 3. Fetch consumable PreKeyBundle from Relay over gRPC (consumes 1 OPK)
            PreKeyBundle remoteBundle;
            using (var scope = initiator.Services.CreateScope())
            {
                var channelFactory = scope.ServiceProvider.GetRequiredService<IPeerGrpcChannelFactory>();
                var relayUri = new Uri($"https://127.0.0.1:{relay.BoundPort}");
                var channel = channelFactory.GetOrCreateChannel(relayUri);
                var client = new UnauthenticatedDeliveryService.UnauthenticatedDeliveryServiceClient(channel);

                var fetchRequest = new FetchPreKeyBundleRequest
                {
                    RecipientRoutingToken = ByteString.CopyFrom(target.MailboxRoutingToken.ToByteArray()),
                    DeliveryToken = ByteString.CopyFrom(target.MailboxDeliveryToken.Span)
                };

                FetchPreKeyBundleResponse fetchResponse;
                try
                {
                    fetchResponse = await client.FetchPreKeyBundleAsync(fetchRequest, cancellationToken: ct);
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound && ex.Status.Detail?.Contains("Unable to consume") == true)
                {
                    _toastService.ShowToast("OPK Depleted", "No available one-time prekeys left on relay.", ToastSeverity.Warning);
                    return false;
                }
                catch (RpcException ex)
                {
                    _toastService.ShowToast("PreKey Missing", $"Failed to fetch prekey from relay: {ex.Status.Detail ?? ex.Message}", ToastSeverity.Error);
                    return false;
                }

                remoteBundle = new PreKeyBundle(
                    target.PublicIdentityId,
                    DeviceId.Primary,
                    IdentityKey.FromSpan(fetchResponse.IdentityKey.Span),
                    DhPublicKey.FromSpan(fetchResponse.SignedPrekey.Span),
                    DeviceLinkProof.FromSpan(fetchResponse.PrekeySignature.Span),
                    fetchResponse.OneTimePrekey != null && !fetchResponse.OneTimePrekey.IsEmpty
                        ? DhPublicKey.FromSpan(fetchResponse.OneTimePrekey.Span)
                        : null,
                    fetchResponse.OneTimePrekeyId);
            }

            // 4. Save mailbox routing capabilities on both peers
            var relayEndpoint = RelayEndpoint.FromHostAndPort("127.0.0.1", relay.BoundPort);

            using (var scope = initiator.Services.CreateScope())
            {
                var routingRepo = scope.ServiceProvider.GetRequiredService<IPeerMailboxRoutingRepository>();
                var cap = new PeerMailboxRoutingCapability(
                    target.PublicIdentityId,
                    target.MailboxRoutingToken,
                    target.MailboxDeliveryToken,
                    relayEndpoint,
                    LastObservedAtUtc: DateTimeOffset.UtcNow);
                await routingRepo.SaveAsync(cap, ct);
            }

            using (var scope = target.Services.CreateScope())
            {
                var routingRepo = scope.ServiceProvider.GetRequiredService<IPeerMailboxRoutingRepository>();
                var cap = new PeerMailboxRoutingCapability(
                    initiator.PublicIdentityId,
                    initiator.MailboxRoutingToken,
                    initiator.MailboxDeliveryToken,
                    relayEndpoint,
                    LastObservedAtUtc: DateTimeOffset.UtcNow);
                await routingRepo.SaveAsync(cap, ct);
            }

            // 5. Initiator initiates X3DH handshake
            using (var scope = initiator.Services.CreateScope())
            {
                var handshakeService = scope.ServiceProvider.GetRequiredService<IHandshakeService>();
                var hsResult = await handshakeService.InitiateHandshakeAsync(
                    initiator.PublicIdentityId,
                    DeviceId.Primary,
                    remoteBundle,
                    ct: ct);

                if (!hsResult.IsSuccess)
                {
                    _toastService.ShowToast("Handshake Failed", hsResult.Error.Description, ToastSeverity.Error);
                    return false;
                }
            }

            // 6. Flush initiator outbox -> dispatches envelope to Relay's UnauthenticatedDeliveryEndpoint.EnqueueMailbox
            var flushResult = await initiator.FlushOutboxAsync(ct);
            if (!flushResult.IsSuccess)
            {
                _toastService.ShowToast("Outbox Failed", flushResult.Error.Description, ToastSeverity.Warning);
                return false;
            }

            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Information,
                initiator.DisplayName,
                initiator.Id,
                "RelayedHandshake",
                $"Relayed handshake initiated: '{initiator.DisplayName}' ➔ '{target.DisplayName}' via relay '{relay.DisplayName}'. Envelope queued."));

            return true;
        }
        catch (Exception ex)
        {
            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Error,
                initiator.DisplayName,
                initiator.Id,
                "RelayedHandshake",
                $"Relayed handshake exception: {ex.Message}",
                ex.ToString()));

            _toastService.ShowToast("Handshake Exception", ex.Message, ToastSeverity.Error);
            return false;
        }
    }
}
