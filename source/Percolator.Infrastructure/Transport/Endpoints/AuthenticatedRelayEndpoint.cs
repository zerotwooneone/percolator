namespace Percolator.Infrastructure.Transport.Endpoints;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
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
    private readonly ConcurrentDictionary<(PublicIdentityId, DeviceId), DateTimeOffset> _publishedTimestamps = new();

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

        await _capabilityStore.RegisterAsync(
            request.RoutingToken.Memory,
            request.AuthorizedDeliveryToken.Memory,
            request.DrainToken.Memory,
            ct: context.CancellationToken);

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

        // 3. Drain and stream existing envelopes
        var envelopes = await _capabilityStore.DrainEnvelopesAsync(authResp.RoutingToken.Memory, context.CancellationToken);
        foreach (var env in envelopes)
        {
            await responseStream.WriteAsync(new MailboxStreamServerMessage { Envelope = env });
        }
    }

    public override async Task<PublishPreKeyBundleResponse> PublishPreKeyBundle(PublishPreKeyBundleRequest request, ServerCallContext context)
    {
        if (request.PayloadBytes.IsEmpty || request.Signature.IsEmpty)
        {
            return new PublishPreKeyBundleResponse { Success = false };
        }

        PreKeyBundlePayloadProto payload;
        try
        {
            payload = PreKeyBundlePayloadProto.Parser.ParseFrom(request.PayloadBytes.Span);
        }
        catch
        {
            return new PublishPreKeyBundleResponse { Success = false };
        }

        if (payload.OwnerIdentityId.Length != 16 || payload.IdentityKey.Length != 32 || payload.SignedPrekey.Length != 32)
        {
            return new PublishPreKeyBundleResponse { Success = false };
        }

        var ownerId = PublicIdentityId.FromBytes(payload.OwnerIdentityId.Span);
        var deviceId = new DeviceId(payload.DeviceId);
        var publishedAtUtc = payload.PublishedAtUtc.ToDateTimeOffset();

        // Rollback / Replay protection: enforce strictly newer published_at timestamp
        if (_publishedTimestamps.TryGetValue((ownerId, deviceId), out var lastPublished))
        {
            if (publishedAtUtc <= lastPublished)
            {
                return new PublishPreKeyBundleResponse { Success = false };
            }
        }

        // Verify Ed25519 signature over payload_bytes
        var identityKey = IdentityKey.FromSpan(payload.IdentityKey.Span);
        bool sigValid = _cryptoEngine.VerifyEd25519Signature(
            identityKey,
            request.PayloadBytes.Span,
            request.Signature.Span);

        if (!sigValid)
        {
            return new PublishPreKeyBundleResponse { Success = false };
        }

        var opks = new List<(uint KeyId, DhPublicKey Key)>();
        foreach (var opk in payload.OneTimePrekeys)
        {
            if (opk.PublicKey.Length == 32)
            {
                opks.Add((opk.KeyId, DhPublicKey.FromSpan(opk.PublicKey.Span)));
            }
        }

        var bundleResult = RelayHostedPreKeyBundle.Create(
            ownerId,
            deviceId,
            identityKey,
            DhPublicKey.FromSpan(payload.SignedPrekey.Span),
            DeviceLinkProof.FromSpan(payload.PrekeySignature.Span),
            opks,
            RelayHostingPolicy.Default,
            _cryptoEngine);

        if (bundleResult.IsFailure)
        {
            return new PublishPreKeyBundleResponse { Success = false };
        }

        await _preKeyRepository.SaveAsync(bundleResult.Value!, context.CancellationToken);
        _publishedTimestamps[(ownerId, deviceId)] = publishedAtUtc;

        return new PublishPreKeyBundleResponse { Success = true };
    }
}
