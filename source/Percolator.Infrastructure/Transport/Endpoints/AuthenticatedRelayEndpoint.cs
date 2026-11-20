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
        if (request.RoutingToken.Length != 16 ||
            request.AuthorizedDeliveryToken.Length != 32 ||
            request.DrainToken.Length != 32 ||
            (request.ExistingDrainToken.Length != 0 && request.ExistingDrainToken.Length != 32))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Routing token, delivery token, and drain token are required."));
        }

        var regResult = await _capabilityStore.RegisterVerifiedAsync(
            BlindedRoutingToken.FromBytes(request.RoutingToken.ToByteArray()),
            DeliveryToken.FromSpan(request.AuthorizedDeliveryToken.Span),
            DeliveryToken.FromSpan(request.DrainToken.Span),
            request.ExistingDrainToken.IsEmpty
                ? null
                : DeliveryToken.FromSpan(request.ExistingDrainToken.Span),
            ct: context.CancellationToken);

        if (regResult.IsFailure)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, regResult.Error.Description));
        }

        return new RegisterMailboxResponse { Success = true };
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
        if (authResp.RoutingToken.Length != 16)
        {
            await responseStream.WriteAsync(new MailboxStreamServerMessage
            {
                AuthResult = new AuthResultProto { Authenticated = false, Reason = "Mailbox not found" }
            });
            return;
        }

        var routingToken = BlindedRoutingToken.FromBytes(authResp.RoutingToken.ToByteArray());
        var drainToken = await _capabilityStore.GetDrainTokenAsync(routingToken, context.CancellationToken);
        if (drainToken == null)
        {
            await responseStream.WriteAsync(new MailboxStreamServerMessage
            {
                AuthResult = new AuthResultProto { Authenticated = false, Reason = "Mailbox not found" }
            });
            return;
        }

        // Verify HMAC-SHA256(drainToken, nonce)
        using var hmac = new HMACSHA256(drainToken.Span.ToArray());
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

        // 3. Setup live stream & send initial queued envelopes without destroying them prematurely.
        // The live channel is bounded (HAZ-INGRESS-RESOURCE-EXHAUSTION): the capability
        // store persists before notifying subscribers, so a full channel drops only the
        // live notification — the envelope still arrives on the next connect's replay.
        var liveChannel = Channel.CreateBounded<RelayQueuedEnvelopeRecord>(
            new BoundedChannelOptions(256)
            {
                SingleWriter = false,
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait
            });

        using var subscription = _capabilityStore.SubscribeMailbox(routingToken, liveChannel.Writer);

        var initialEnvelopes = await _capabilityStore.GetEnvelopesAsync(routingToken, context.CancellationToken);
        var initialQueueIds = new HashSet<MailboxQueueId>();

        foreach (var env in initialEnvelopes)
        {
            initialQueueIds.Add(env.QueueId);
            await responseStream.WriteAsync(new MailboxStreamServerMessage { Envelope = ToProto(env) });
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);

        var readClientAcksTask = Task.Run(async () =>
        {
            while (await requestStream.MoveNext(cts.Token).ConfigureAwait(false))
            {
                var msg = requestStream.Current;
                if (msg.PayloadCase == MailboxStreamClientMessage.PayloadOneofCase.Ack &&
                    msg.Ack != null &&
                    msg.Ack.Acknowledged &&
                    !string.IsNullOrEmpty(msg.Ack.QueueId))
                {
                    var ackQueueId = MailboxQueueId.Create(msg.Ack.QueueId);
                    if (ackQueueId.IsSuccess)
                    {
                        await _capabilityStore.AcknowledgeEnvelopeAsync(
                            routingToken,
                            ackQueueId.Value!,
                            cts.Token).ConfigureAwait(false);
                    }
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
                        new MailboxStreamServerMessage { Envelope = ToProto(liveEnv) }).ConfigureAwait(false);
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
            request.SignedPrekeyId == 0 ||
            request.SignedPrekey.Length != 32 ||
            request.PrekeySignature.Length != 64)
        {
            return new PublishPreKeyBundleResponse { Success = false };
        }

        // Mailbox ownership: a valid drain token for routing_token is required —
        // the drain token is the only credential that authorizes overwriting this
        // mailbox's hosted bundle.
        var routingToken = BlindedRoutingToken.FromBytes(request.RoutingToken.ToByteArray());
        bool drainValid = await _capabilityStore.VerifyDrainTokenAsync(
            routingToken,
            DeliveryToken.FromSpan(request.DrainToken.Span),
            context.CancellationToken);
        if (!drainValid)
        {
            return new PublishPreKeyBundleResponse { Success = false };
        }

        var deviceId = new DeviceId(request.DeviceId);

        var identityKey = IdentityKey.FromSpan(request.IdentityKey.Span);

        // Collapse duplicate KeyIds within the batch — the merge below only filters
        // against already-hosted ids, so a duplicated id would otherwise enqueue the
        // same key twice and ConsumeBundle could serve a consumed id to a second
        // fetcher. Keep the first occurrence.
        var opks = new List<(uint KeyId, DhPublicKey Key)>();
        var seenKeyIds = new HashSet<uint>();
        foreach (var opk in request.OneTimePrekeys)
        {
            if (opk.PublicKey.Length == 32 && seenKeyIds.Add(opk.KeyId))
            {
                opks.Add((opk.KeyId, DhPublicKey.FromSpan(opk.PublicKey.Span)));
            }
        }

        var signedPreKey = DhPublicKey.FromSpan(request.SignedPrekey.Span);
        var signedPreKeySignature = DeviceLinkProof.FromSpan(request.PrekeySignature.Span);
        var signedPreKeyId = new SignedPreKeyId(request.SignedPrekeyId);

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
                signedPreKeyId,
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
                signedPreKeyId,
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

    public override async Task<GetPreKeyStatusResponse> GetPreKeyStatus(GetPreKeyStatusRequest request, ServerCallContext context)
    {
        if (request.RoutingToken.Length != 16 ||
            request.DrainToken.Length != 32 ||
            request.DeviceId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Routing token, drain token, and device id are required."));
        }

        var routingToken = BlindedRoutingToken.FromBytes(request.RoutingToken.ToByteArray());
        bool drainValid = await _capabilityStore.VerifyDrainTokenAsync(
            routingToken,
            DeliveryToken.FromSpan(request.DrainToken.Span),
            context.CancellationToken);
        if (!drainValid)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Invalid drain token capability."));
        }

        var bundle = await _preKeyRepository.GetAsync(
            routingToken,
            new DeviceId(request.DeviceId),
            context.CancellationToken);

        if (bundle == null)
        {
            return new GetPreKeyStatusResponse { Found = false };
        }

        return new GetPreKeyStatusResponse
        {
            Found = true,
            OneTimePrekeyCount = (uint)bundle.AvailableOneTimePreKeyCount,
            SignedPrekeyId = bundle.SignedPreKeyId.Value
        };
    }

    private static QueuedSealedEnvelopeProto ToProto(RelayQueuedEnvelopeRecord record) => new()
    {
        QueueId = record.QueueId.Value,
        EnvelopePayload = ByteString.CopyFrom(record.EnvelopePayload.Span),
        EnqueuedAtUtc = Timestamp.FromDateTimeOffset(record.EnqueuedAtUtc)
    };
}
