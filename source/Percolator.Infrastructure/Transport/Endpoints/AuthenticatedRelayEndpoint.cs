namespace Percolator.Infrastructure.Transport.Endpoints;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Transport;

public sealed class AuthenticatedRelayEndpoint : AuthenticatedRelayService.AuthenticatedRelayServiceBase
{
    private readonly IRelayMailboxCapabilityStore _capabilityStore;
    private readonly IRelayPreKeyRepository _preKeyRepository;
    private readonly ICryptoEngine _cryptoEngine;

    public AuthenticatedRelayEndpoint(
        IRelayMailboxCapabilityStore capabilityStore,
        IRelayPreKeyRepository preKeyRepository,
        ICryptoEngine cryptoEngine)
    {
        _capabilityStore = capabilityStore ?? throw new ArgumentNullException(nameof(capabilityStore));
        _preKeyRepository = preKeyRepository ?? throw new ArgumentNullException(nameof(preKeyRepository));
        _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));
    }

    public override async Task<RegisterMailboxResponse> RegisterMailbox(RegisterMailboxRequest request, ServerCallContext context)
    {
        if (request.RoutingToken.IsEmpty || request.AuthorizedDeliveryToken.IsEmpty || request.DrainToken.IsEmpty)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Routing token, delivery token, and drain token are required."));
        }

        var regResult = await _capabilityStore.RegisterVerifiedAsync(
            request.RoutingToken.Memory,
            request.AuthorizedDeliveryToken.Memory,
            request.DrainToken.Memory,
            request.ExistingDrainToken.Memory,
            ct: context.CancellationToken);

        if (regResult.IsFailure)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, regResult.Error.Description));
        }

        return new RegisterMailboxResponse { Success = true };
    }

    public override async Task<DrainMailboxResponse> DrainMailbox(DrainMailboxRequest request, ServerCallContext context)
    {
        if (request.RoutingToken.IsEmpty || request.DrainToken.IsEmpty)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Routing token and drain token are required."));
        }

        bool isAuthorized = await _capabilityStore.VerifyDrainTokenAsync(
            request.RoutingToken.Memory,
            request.DrainToken.Memory,
            context.CancellationToken);

        if (!isAuthorized)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Invalid drain token."));
        }

        var envelopes = await _capabilityStore.DrainEnvelopesAsync(request.RoutingToken.Memory, context.CancellationToken);
        var response = new DrainMailboxResponse();
        response.Envelopes.AddRange(envelopes);
        return response;
    }

    public override async Task ConnectMailboxStream(
        IAsyncStreamReader<MailboxStreamClientMessage> requestStream,
        IServerStreamWriter<MailboxStreamServerMessage> responseStream,
        ServerCallContext context)
    {
        // 1. Send CSPRNG auth challenge
        byte[] nonce = new byte[32];
        RandomNumberGenerator.Fill(nonce);

        var challenge = new AuthChallengeProto
        {
            Nonce = ByteString.CopyFrom(nonce),
            ExpiresAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(1))
        };

        await responseStream.WriteAsync(new MailboxStreamServerMessage { AuthChallenge = challenge });

        // 2. Read client auth response
        if (!await requestStream.MoveNext(context.CancellationToken))
        {
            return;
        }

        var clientMessage = requestStream.Current;
        if (clientMessage.PayloadCase != MailboxStreamClientMessage.PayloadOneofCase.AuthResponse)
        {
            await responseStream.WriteAsync(new MailboxStreamServerMessage
            {
                AuthResult = new AuthResultProto { Authenticated = false, Reason = "Expected AuthResponse" }
            });
            return;
        }

        var authResp = clientMessage.AuthResponse;
        var drainToken = await _capabilityStore.GetDrainTokenAsync(authResp.RoutingToken.Memory, context.CancellationToken);
        if (!drainToken.HasValue)
        {
            await responseStream.WriteAsync(new MailboxStreamServerMessage
            {
                AuthResult = new AuthResultProto { Authenticated = false, Reason = "Mailbox not found" }
            });
            return;
        }

        // Verify HMAC-SHA256(drainToken, nonce)
        using var hmac = new HMACSHA256(drainToken.Value.ToArray());
        byte[] expectedHmac = hmac.ComputeHash(nonce);

        if (!CryptographicOperations.FixedTimeEquals(expectedHmac, authResp.ChallengeHmac.Span))
        {
            await responseStream.WriteAsync(new MailboxStreamServerMessage
            {
                AuthResult = new AuthResultProto { Authenticated = false, Reason = "HMAC authentication failed" }
            });
            return;
        }

        await responseStream.WriteAsync(new MailboxStreamServerMessage
        {
            AuthResult = new AuthResultProto { Authenticated = true }
        });

        // 3. Setup live stream & send initial queued envelopes without destroying them prematurely
        var liveChannel = Channel.CreateUnbounded<QueuedSealedEnvelopeProto>(
            new UnboundedChannelOptions
            {
                SingleWriter = false,
                SingleReader = true
            });

        using var subscription = _capabilityStore.SubscribeMailbox(authResp.RoutingToken.Memory, liveChannel.Writer);

        var initialEnvelopes = await _capabilityStore.GetEnvelopesAsync(authResp.RoutingToken.Memory, context.CancellationToken);
        var initialQueueIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var env in initialEnvelopes)
        {
            initialQueueIds.Add(env.QueueId);
            await responseStream.WriteAsync(new MailboxStreamServerMessage { Envelope = env });
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);

        var readClientAcksTask = Task.Run(async () =>
        {
            while (await requestStream.MoveNext(cts.Token).ConfigureAwait(false))
            {
                var msg = requestStream.Current;
                if (msg.PayloadCase == MailboxStreamClientMessage.PayloadOneofCase.Ack && msg.Ack != null)
                {
                    await _capabilityStore.AcknowledgeEnvelopeAsync(
                        authResp.RoutingToken.Memory,
                        msg.Ack.QueueId,
                        cts.Token).ConfigureAwait(false);
                }
            }
        }, cts.Token);

        var writeServerLiveEnvelopesTask = Task.Run(async () =>
        {
            while (await liveChannel.Reader.WaitToReadAsync(cts.Token).ConfigureAwait(false))
            {
                while (liveChannel.Reader.TryRead(out var liveEnv))
                {
                    // Avoid duplicate if envelope was already sent as part of initial offline batch
                    if (initialQueueIds.Count > 0 && initialQueueIds.Remove(liveEnv.QueueId))
                    {
                        continue;
                    }

                    await responseStream.WriteAsync(
                        new MailboxStreamServerMessage { Envelope = liveEnv }).ConfigureAwait(false);
                }
            }
        }, cts.Token);

        await Task.WhenAny(readClientAcksTask, writeServerLiveEnvelopesTask).ConfigureAwait(false);
        cts.Cancel();
        liveChannel.Writer.TryComplete();

        try
        {
            await Task.WhenAll(readClientAcksTask, writeServerLiveEnvelopesTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Expected cooperative cancellation on stream completion/closure
        }
    }

    public override async Task<PublishPreKeyBundleResponse> PublishPreKeyBundle(PublishPreKeyBundleRequest request, ServerCallContext context)
    {
        // Flattened request: storage is keyed by (routing_token, device_id). The relay
        // never learns the owner's public identity — authorization is the drain token,
        // key-material integrity is the bundle's own signed-prekey signature verified
        // inside RelayHostedPreKeyBundle.Create/UpdateSignedPreKey.
        if (request.RoutingToken.Length != 16 ||
            request.DrainToken.Length != 32 ||
            request.DeviceId == 0 ||
            request.IdentityKey.Length != 32 ||
            request.SignedPrekey.Length != 32 ||
            request.PrekeySignature.Length != 64)
        {
            return new PublishPreKeyBundleResponse { Success = false };
        }

        // Mailbox ownership: a valid drain token for routing_token is required —
        // the drain token is the only credential that authorizes overwriting this
        // mailbox's hosted bundle.
        bool drainValid = await _capabilityStore.VerifyDrainTokenAsync(
            request.RoutingToken.Memory,
            request.DrainToken.Memory,
            context.CancellationToken);
        if (!drainValid)
        {
            return new PublishPreKeyBundleResponse { Success = false };
        }

        var routingToken = BlindedRoutingToken.FromBytes(request.RoutingToken.ToByteArray());
        var deviceId = new DeviceId(request.DeviceId);

        var identityKey = IdentityKey.FromSpan(request.IdentityKey.Span);

        var opks = new List<(uint KeyId, DhPublicKey Key)>();
        foreach (var opk in request.OneTimePrekeys)
        {
            if (opk.PublicKey.Length == 32)
            {
                opks.Add((opk.KeyId, DhPublicKey.FromSpan(opk.PublicKey.Span)));
            }
        }

        var signedPreKey = DhPublicKey.FromSpan(request.SignedPrekey.Span);
        var signedPreKeySignature = DeviceLinkProof.FromSpan(request.PrekeySignature.Span);

        // Server-side OPK replenishment: an existing bundle gains only new KeyIds and a
        // rotated signed prekey; a missing bundle is created wholesale.
        var existing = await _preKeyRepository.GetAsync(routingToken, deviceId, context.CancellationToken);
        if (existing == null)
        {
            var bundleResult = RelayHostedPreKeyBundle.Create(
                routingToken,
                deviceId,
                identityKey,
                signedPreKey,
                signedPreKeySignature,
                opks,
                RelayHostingPolicy.Default,
                _cryptoEngine);

            if (bundleResult.IsFailure)
            {
                return new PublishPreKeyBundleResponse { Success = false };
            }

            await _preKeyRepository.SaveAsync(bundleResult.Value!, context.CancellationToken);
        }
        else
        {
            if (!CryptographicOperations.FixedTimeEquals(existing.IdentityKey.Span, identityKey.Span))
            {
                return new PublishPreKeyBundleResponse { Success = false };
            }

            var updateSpk = existing.UpdateSignedPreKey(
                signedPreKey,
                signedPreKeySignature,
                RelayHostingPolicy.Default,
                _cryptoEngine);
            if (updateSpk.IsFailure)
            {
                return new PublishPreKeyBundleResponse { Success = false };
            }

            var existingKeyIds = new HashSet<uint>(existing.OneTimePreKeys.Select(k => k.KeyId));
            var newOpks = opks.Where(k => !existingKeyIds.Contains(k.KeyId)).ToList();
            var replenish = existing.ReplenishOneTimePreKeys(newOpks, RelayHostingPolicy.Default);
            if (replenish.IsFailure)
            {
                return new PublishPreKeyBundleResponse { Success = false };
            }

            await _preKeyRepository.SaveAsync(existing, context.CancellationToken);
        }

        return new PublishPreKeyBundleResponse { Success = true };
    }
}
