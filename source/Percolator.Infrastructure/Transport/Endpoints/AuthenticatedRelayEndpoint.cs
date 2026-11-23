namespace Percolator.Infrastructure.Transport.Endpoints;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Protos;

public sealed class AuthenticatedRelayEndpoint : AuthenticatedRelayService.AuthenticatedRelayServiceBase
{
    private readonly IRelayMailboxCapabilityStore _capabilityStore;
    private readonly IRelayPreKeyRepository _preKeyRepository;
    private readonly ICryptoEngine _cryptoEngine;
    private readonly IRelayHostingPolicyProvider _policyProvider;
    private readonly ILocalRelayAssociationRepository _localAssociationRepo;
    private readonly IPendingMailboxRegistrationRepository _pendingRegistrationRepo;
    private readonly IAuthenticatedMemberRateLimiter _rateLimiter;
    private readonly PublicIdentityId _relayIdentityId;

    public AuthenticatedRelayEndpoint(
        IRelayMailboxCapabilityStore capabilityStore,
        IRelayPreKeyRepository preKeyRepository,
        ICryptoEngine cryptoEngine,
        IRelayHostingPolicyProvider policyProvider,
        ILocalRelayAssociationRepository localAssociationRepo,
        IPendingMailboxRegistrationRepository pendingRegistrationRepo,
        IAuthenticatedMemberRateLimiter rateLimiter,
        PublicIdentityId? relayIdentityId = null)
    {
        _capabilityStore = capabilityStore ?? throw new ArgumentNullException(nameof(capabilityStore));
        _preKeyRepository = preKeyRepository ?? throw new ArgumentNullException(nameof(preKeyRepository));
        _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));
        _policyProvider = policyProvider ?? throw new ArgumentNullException(nameof(policyProvider));
        _localAssociationRepo = localAssociationRepo ?? throw new ArgumentNullException(nameof(localAssociationRepo));
        _pendingRegistrationRepo = pendingRegistrationRepo ?? throw new ArgumentNullException(nameof(pendingRegistrationRepo));
        _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
        _relayIdentityId = relayIdentityId ?? PublicIdentityId.New();
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

        var policy = _policyProvider.GetPolicy();
        var routingToken = BlindedRoutingToken.FromBytes(request.RoutingToken.ToByteArray());
        HttpContext? httpContext = null;
        try
        {
            httpContext = context.GetHttpContext();
        }
        catch (InvalidOperationException)
        {
            // Non-ASP.NET Core ServerCallContext (e.g. test harness)
        }
        var remoteIp = httpContext?.Connection?.RemoteIpAddress;
        bool isLoopback = remoteIp != null && IPAddress.IsLoopback(remoteIp);

        // 1. AutoApproveLocalhost fast-path: pre-associated local Personas over loopback auto-approve
        if (policy.AutoApproveLocalhost && isLoopback && await _localAssociationRepo.IsAuthorizedAsync(routingToken, context.CancellationToken))
        {
            return await ProvisionMailboxAsync(request, routingToken, context);
        }

        // 2. Closed registration strategy -> silently hang up
        if (policy.RegistrationStrategy == RelayRegistrationStrategy.Closed)
        {
            httpContext?.Abort();
            throw new RpcException(new Status(StatusCode.Unavailable, string.Empty));
        }

        // 3. Rejected or Blocked applicants -> silently hang up
        var pending = await _pendingRegistrationRepo.GetByApplicantRoutingTokenAsync(routingToken, context.CancellationToken);
        if (pending != null && (pending.Status == RegistrationModerationStatus.Rejected || pending.Status == RegistrationModerationStatus.Blocked))
        {
            httpContext?.Abort();
            throw new RpcException(new Status(StatusCode.Unavailable, string.Empty));
        }

        // 4. ApprovalRequired strategy
        if (policy.RegistrationStrategy == RelayRegistrationStrategy.ApprovalRequired)
        {
            if (pending != null && pending.Status == RegistrationModerationStatus.Approved)
            {
                return await ProvisionMailboxAsync(request, routingToken, context);
            }

            // Stage into PendingMailboxRegistrations
            var registration = new PendingMailboxRegistration(
                _relayIdentityId,
                routingToken,
                RegistrationModerationStatus.PendingApproval,
                DateTimeOffset.UtcNow);

            await _pendingRegistrationRepo.SaveAsync(registration, context.CancellationToken);

            return new RegisterMailboxResponse
            {
                Success = true,
                RegistrationStatus = RegistrationStatusProto.RegistrationStatusPendingApproval
            };
        }

        // 5. Open strategy
        return await ProvisionMailboxAsync(request, routingToken, context);
    }

    private async Task<RegisterMailboxResponse> ProvisionMailboxAsync(
        RegisterMailboxRequest request,
        BlindedRoutingToken routingToken,
        ServerCallContext context)
    {
        var regResult = await _capabilityStore.RegisterVerifiedAsync(
            routingToken,
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

        return new RegisterMailboxResponse
        {
            Success = true,
            RegistrationStatus = RegistrationStatusProto.RegistrationStatusApproved
        };
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

        byte[] drainTokenBytes = drainToken.Span.ToArray();
        byte[] expectedHmac;
        try
        {
            using var hmac = new HMACSHA256(drainTokenBytes);
            expectedHmac = hmac.ComputeHash(nonce);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(drainTokenBytes);
        }

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
        var liveChannel = Channel.CreateBounded<RelayQueuedEnvelopeRecord>(
            new BoundedChannelOptions(256)
            {
                SingleWriter = false,
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait
            });

        using var subscription = _capabilityStore.SubscribeMailbox(routingToken, liveChannel.Writer);

        var queued = await _capabilityStore.GetEnvelopesAsync(routingToken, context.CancellationToken);
        foreach (var envelope in queued)
        {
            await responseStream.WriteAsync(new MailboxStreamServerMessage { Envelope = ToProto(envelope) });
        }

        // 4. Run read loop & write loop concurrently
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);

        var readTask = Task.Run(async () =>
        {
            try
            {
                while (await requestStream.MoveNext(linkedCts.Token))
                {
                    var msg = requestStream.Current;
                    if (msg.PayloadCase == MailboxStreamClientMessage.PayloadOneofCase.Ack &&
                        msg.Ack.Acknowledged &&
                        !string.IsNullOrWhiteSpace(msg.Ack.QueueId))
                    {
                        var queueId = MailboxQueueId.Parse(msg.Ack.QueueId);
                        await _capabilityStore.AcknowledgeEnvelopeAsync(routingToken, queueId, linkedCts.Token);
                    }
                }
            }
            catch (OperationCanceledException) { }
        });

        var writeTask = Task.Run(async () =>
        {
            try
            {
                while (await liveChannel.Reader.WaitToReadAsync(linkedCts.Token))
                {
                    while (liveChannel.Reader.TryRead(out var liveEnvelope))
                    {
                        await responseStream.WriteAsync(new MailboxStreamServerMessage { Envelope = ToProto(liveEnvelope) });
                    }
                }
            }
            catch (OperationCanceledException) { }
        });

        await Task.WhenAny(readTask, writeTask);
        linkedCts.Cancel();
        await Task.WhenAll(readTask, writeTask);
    }

    public override async Task<PublishPreKeyBundleResponse> PublishPreKeyBundle(PublishPreKeyBundleRequest request, ServerCallContext context)
    {
        var policy = _policyProvider.GetPolicy();
        if (!policy.IsFeatureEnabled(RelayFeatureFlags.PreKeys))
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "PreKeys feature is disabled on this relay."));
        }

        if (request.RoutingToken.Length != 16 ||
            request.DrainToken.Length != 32 ||
            request.DeviceId == 0 ||
            request.IdentityKey.Length != 32 ||
            request.SignedPrekey.Length != 32 ||
            request.PrekeySignature.Length != 64 ||
            request.SignedPrekeyId == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "All bundle fields are required and must have exact sizes."));
        }

        var routingToken = BlindedRoutingToken.FromBytes(request.RoutingToken.ToByteArray());
        byte[] drainTokenBytes = request.DrainToken.ToByteArray();
        try
        {
            bool drainValid = await _capabilityStore.VerifyDrainTokenAsync(
                routingToken,
                DeliveryToken.FromSpan(drainTokenBytes),
                context.CancellationToken);
            if (!drainValid)
            {
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Invalid drain token capability."));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(drainTokenBytes);
        }

        var deviceId = new DeviceId(request.DeviceId);
        var identityKey = IdentityKey.FromSpan(request.IdentityKey.Span);

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
                policy,
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
                policy,
                _cryptoEngine);
            if (updateSpk.IsFailure)
            {
                return new PublishPreKeyBundleResponse { Success = false };
            }

            var existingKeyIds = new HashSet<uint>(existing.OneTimePreKeys.Select(k => k.KeyId));
            var newOpks = opks.Where(k => !existingKeyIds.Contains(k.KeyId)).ToList();
            var replenish = existing.ReplenishOneTimePreKeys(newOpks, policy);
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
        byte[] drainTokenBytes = request.DrainToken.ToByteArray();
        try
        {
            bool drainValid = await _capabilityStore.VerifyDrainTokenAsync(
                routingToken,
                DeliveryToken.FromSpan(drainTokenBytes),
                context.CancellationToken);
            if (!drainValid)
            {
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Invalid drain token capability."));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(drainTokenBytes);
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

    public override async Task<PreKeyBundleProto> FetchPreKeyBundle(FetchPreKeyBundleRequestProto request, ServerCallContext context)
    {
        var policy = _policyProvider.GetPolicy();
        if (!policy.IsFeatureEnabled(RelayFeatureFlags.PreKeys))
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "PreKeys feature is disabled on this relay."));
        }

        if (request.RoutingToken.Length != 16 || request.DrainToken.Length != 32 || request.TargetRoutingToken.Length != 16)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Routing token, drain token, and target routing token are required."));
        }

        var callerRoutingToken = BlindedRoutingToken.FromBytes(request.RoutingToken.ToByteArray());
        byte[] drainTokenBytes = request.DrainToken.ToByteArray();
        try
        {
            bool callerValid = await _capabilityStore.VerifyDrainTokenAsync(
                callerRoutingToken,
                DeliveryToken.FromSpan(drainTokenBytes),
                context.CancellationToken);
            if (!callerValid)
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Caller credentials are invalid."));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(drainTokenBytes);
        }

        // Per-member sliding window rate limit
        bool allowed = await _rateLimiter.TryConsumeAsync(callerRoutingToken, "FetchPreKeyBundle", context.CancellationToken);
        if (!allowed)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "Rate limit exceeded for FetchPreKeyBundle."));
        }

        var targetRoutingToken = BlindedRoutingToken.FromBytes(request.TargetRoutingToken.ToByteArray());
        var targetDevice = request.DeviceId != 0 ? new DeviceId(request.DeviceId) : DeviceId.Primary;

        var consumed = await _preKeyRepository.ConsumePreKeyBundleAsync(targetRoutingToken, targetDevice, context.CancellationToken);
        if (consumed == null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Target pre-key bundle not found."));
        }

        var response = new PreKeyBundleProto
        {
            IdentityKey = ByteString.CopyFrom(consumed.IdentityKey.Span),
            SignedPrekey = ByteString.CopyFrom(consumed.SignedPreKey.Span),
            PrekeySignature = ByteString.CopyFrom(consumed.SignedPreKeySignature.Span),
            SignedPrekeyId = consumed.SignedPreKeyId.Value
        };

        if (consumed.OneTimePreKey != null)
        {
            response.OneTimePrekey = ByteString.CopyFrom(consumed.OneTimePreKey.Span);
            response.OneTimePrekeyId = consumed.OneTimePreKeyId;
        }

        return response;
    }

    public override async Task<EnqueueMailboxResponseProto> SendInitialHandshake(SendInitialHandshakeRequestProto request, ServerCallContext context)
    {
        var policy = _policyProvider.GetPolicy();
        if (request.RoutingToken.Length != 16 || request.DrainToken.Length != 32 || request.RecipientRoutingToken.Length != 16)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Routing token, drain token, and recipient routing token are required."));
        }

        var callerRoutingToken = BlindedRoutingToken.FromBytes(request.RoutingToken.ToByteArray());
        byte[] drainTokenBytes = request.DrainToken.ToByteArray();
        try
        {
            bool callerValid = await _capabilityStore.VerifyDrainTokenAsync(
                callerRoutingToken,
                DeliveryToken.FromSpan(drainTokenBytes),
                context.CancellationToken);
            if (!callerValid)
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Caller credentials are invalid."));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(drainTokenBytes);
        }

        // Per-member sliding window rate limit
        bool allowed = await _rateLimiter.TryConsumeAsync(callerRoutingToken, "SendInitialHandshake", context.CancellationToken);
        if (!allowed)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "Rate limit exceeded for SendInitialHandshake."));
        }

        if (request.Envelope == null || request.Envelope.EncryptedPackage.Length > policy.MaxMailboxBytes)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "Envelope size exceeds relay mailbox quota."));
        }

        var recipientRoutingToken = BlindedRoutingToken.FromBytes(request.RecipientRoutingToken.ToByteArray());
        var now = DateTimeOffset.UtcNow;
        var envelope = new MailboxEnvelope(
            EnvelopeId.New(),
            recipientRoutingToken,
            request.Envelope.ToByteArray(),
            now,
            now.Add(policy.EnvelopeTtl));

        var result = await _capabilityStore.EnqueueAuthenticatedInitialAsync(
            recipientRoutingToken,
            envelope,
            policy.MaxMailboxBytes,
            context.CancellationToken);

        if (result.IsFailure)
        {
            var statusCode = result.Error.Code == "MAILBOX_QUOTA_EXCEEDED"
                ? StatusCode.ResourceExhausted
                : StatusCode.PermissionDenied;
            throw new RpcException(new Status(statusCode, result.Error.Description));
        }

        return new EnqueueMailboxResponseProto { Success = true };
    }

    private static QueuedSealedEnvelopeProto ToProto(RelayQueuedEnvelopeRecord record) => new()
    {
        QueueId = record.QueueId.Value,
        EnvelopePayload = ByteString.CopyFrom(record.EnvelopePayload.Span),
        EnqueuedAtUtc = Timestamp.FromDateTimeOffset(record.EnqueuedAtUtc)
    };
}
