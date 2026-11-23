using System.Security.Cryptography;
using Percolator.Application.Delivery;
using Percolator.Application.Delivery.Ports;
using Percolator.Application.Delivery.Events;
using Percolator.Application.Ingress;
using Percolator.Application.Ports;
using Percolator.Application.Profiles;
using Percolator.Application.Routing;
using Percolator.Application.Security;
using Percolator.PluginSdk.Events;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.PluginSdk;
using DeliveryRoute = Percolator.Application.Delivery.DeliveryRoute;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Application.Handshake;

public sealed class HandshakeService : IHandshakeService
{
    private readonly ILocalIdentityKeyStore _identityKeyStore;
    private readonly IPrivatePreKeyStore _preKeyStore;
    private readonly IPeerContactRepository _contactRepo;
    private readonly IContactRequestCoordinator _contactCoordinator;
    private readonly IRatchetSessionRepository _sessionRepo;
    private readonly IPendingHandshakeRepository _pendingHandshakeRepo;
    private readonly IHandshakeReplayFilter _replayFilter;
    private readonly IOutboxRepository _outboxRepo;
    private readonly ICryptoEngine _cryptoEngine;
    private readonly IDateTimeProvider _timeProvider;
    private readonly IAppRouter _appRouter;
    private readonly IEventPublisher _eventPublisher;
    private readonly ISessionWirePacker _wirePacker;
    private readonly IRoutingCoordinator _routingCoordinator;

    public HandshakeService(
        ILocalIdentityKeyStore identityKeyStore,
        IPrivatePreKeyStore preKeyStore,
        IPeerContactRepository contactRepo,
        IContactRequestCoordinator contactCoordinator,
        IRatchetSessionRepository sessionRepo,
        IPendingHandshakeRepository pendingHandshakeRepo,
        IHandshakeReplayFilter replayFilter,
        IOutboxRepository outboxRepo,
        ICryptoEngine cryptoEngine,
        IDateTimeProvider timeProvider,
        IAppRouter appRouter,
        IEventPublisher eventPublisher,
        ISessionWirePacker wirePacker,
        IRoutingCoordinator routingCoordinator)
    {
        _identityKeyStore = identityKeyStore ?? throw new ArgumentNullException(nameof(identityKeyStore));
        _preKeyStore = preKeyStore ?? throw new ArgumentNullException(nameof(preKeyStore));
        _contactRepo = contactRepo ?? throw new ArgumentNullException(nameof(contactRepo));
        _contactCoordinator = contactCoordinator ?? throw new ArgumentNullException(nameof(contactCoordinator));
        _sessionRepo = sessionRepo ?? throw new ArgumentNullException(nameof(sessionRepo));
        _pendingHandshakeRepo = pendingHandshakeRepo ?? throw new ArgumentNullException(nameof(pendingHandshakeRepo));
        _replayFilter = replayFilter ?? throw new ArgumentNullException(nameof(replayFilter));
        _outboxRepo = outboxRepo ?? throw new ArgumentNullException(nameof(outboxRepo));
        _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _appRouter = appRouter ?? throw new ArgumentNullException(nameof(appRouter));
        _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
        _wirePacker = wirePacker ?? throw new ArgumentNullException(nameof(wirePacker));
        _routingCoordinator = routingCoordinator ?? throw new ArgumentNullException(nameof(routingCoordinator));
    }

    public async ValueTask<DomainResult<DirectRatchetSession>> InitiateHandshakeAsync(
        PublicIdentityId ownerId,
        DeviceId ownerDeviceId,
        PublicIdentityId remotePeerId,
        RelayEndpoint relayEndpoint,
        PreKeyBundle remoteBundle,
        AppId? initialAppId = null,
        ReadOnlyMemory<byte>? initialPayload = null,
        CancellationToken ct = default)
    {
        if (remoteBundle == null)
        {
            return DomainResult<DirectRatchetSession>.Failure(
                new DomainError("NULL_PREKEY_BUNDLE", "Remote pre-key bundle cannot be null."));
        }
        if (relayEndpoint == null)
        {
            return DomainResult<DirectRatchetSession>.Failure(
                new DomainError("MISSING_RELAY_ENDPOINT", "RelayEndpoint cannot be null."));
        }

        using var identityPrivateKey = await _identityKeyStore.GetIdentityPrivateKeyAsync(ownerId, ct);
        if (identityPrivateKey == null)
        {
            return DomainResult<DirectRatchetSession>.Failure(
                new DomainError("IDENTITY_KEY_NOT_FOUND", "Local identity private key is missing."));
        }

        var ownerPublicKey = await _identityKeyStore.GetIdentityPublicKeyAsync(ownerId, ct);
        if (ownerPublicKey == null)
        {
            return DomainResult<DirectRatchetSession>.Failure(
                new DomainError("IDENTITY_KEY_NOT_FOUND", "Local identity public key is missing."));
        }

        var x3dhResult = X3dhAgreement.Initiate(identityPrivateKey.Span, ownerPublicKey, remoteBundle, _cryptoEngine);
        if (!x3dhResult.IsSuccess)
        {
            return DomainResult<DirectRatchetSession>.Failure(x3dhResult.Error!);
        }

        var initiatorResult = x3dhResult.Value!;
        var sessionResult = DirectRatchetSession.CreateFromX3dhInitiator(
            ownerId,
            ownerDeviceId,
            remotePeerId,
            remoteBundle.DeviceId,
            initiatorResult,
            remoteBundle.SignedPreKey,
            _cryptoEngine);

        if (!sessionResult.IsSuccess)
        {
            return sessionResult;
        }

        var session = sessionResult.Value!;

        byte[]? encryptedInitialPayload = null;
        if (initialAppId.HasValue && initialPayload.HasValue && initialPayload.Value.Length > 0)
        {
            var stepResult = session.StepSendingChain(_cryptoEngine);
            if (!stepResult.IsSuccess)
            {
                return DomainResult<DirectRatchetSession>.Failure(stepResult.Error!);
            }

            var (counter, messageKey, _) = stepResult.Value;
            var inner = new byte[1 + initialPayload.Value.Length];
            inner[0] = initialAppId.Value.Value;
            initialPayload.Value.Span.CopyTo(inner.AsSpan(1));

            var padded = PayloadPadding.ApplyPadding(inner);
            try
            {
                using (messageKey)
                using (var expanded = messageKey.Expand(counter))
                {
                    encryptedInitialPayload = _cryptoEngine.EncryptAesGcm(
                        expanded.CipherKey,
                        expanded.Nonce,
                        padded,
                        ReadOnlySpan<byte>.Empty);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(inner);
                CryptographicOperations.ZeroMemory(padded);
            }
        }

        await _sessionRepo.SaveSessionAsync(session, ct);

        // Assemble handshake outbox package
        uint signedPreKeyId = remoteBundle.SignedPreKeyId.Value;
        uint? oneTimePreKeyId = remoteBundle.OneTimePreKey != null ? remoteBundle.OneTimePreKeyId : null;

        var header = new HandshakeHeader(
            ownerPublicKey,
            initiatorResult.EphemeralPublicKey,
            signedPreKeyId,
            oneTimePreKeyId);

        var packed = _wirePacker.PackHandshakeMessage(header, encryptedInitialPayload ?? ReadOnlyMemory<byte>.Empty);
        var handshakePackage = packed.ToArray();

        var channelId = ChannelId.DeterministicForDirect(ownerId, remotePeerId, relayEndpoint);
        var routeResult = await _routingCoordinator.ResolveRouteAsync(remotePeerId, relayEndpoint, ct).ConfigureAwait(false);
        var routingToken = routeResult.IsSuccess
            ? routeResult.Value.TargetRoutingToken
            : BlindedRoutingToken.New();

        var route = new DeliveryRoute(
            DeliveryRouteType.RelayedOneToOne,
            routingToken,
            relayEndpoint,
            TargetDeliveryToken: null);

        var jobResult = OutboxJob.Create(
            channelId,
            ownerId,
            remotePeerId,
            route,
            handshakePackage,
            _timeProvider);

        if (jobResult.IsSuccess)
        {
            await _outboxRepo.SaveAsync(jobResult.Value!, ct);
            await _eventPublisher.PublishAsync(
                new OutboxJobEnqueuedEvent(jobResult.Value!.Id, ownerId, _timeProvider.UtcNow), ct);
        }

        return DomainResult<DirectRatchetSession>.Success(session);
    }

    public async ValueTask<DomainResult> ReceiveInvitationAsync(
        InboundHandshakeEnvelope envelope,
        CancellationToken ct = default)
    {
        if (!_replayFilter.TryRecordAndValidate(envelope.SenderEphemeralKey, envelope.ReceivedAtUtc))
        {
            return DomainResult.Failure(new DomainError(
                "HANDSHAKE_REPLAY_DETECTED", "Handshake invitation contains duplicate or replayed ephemeral key."));
        }

        var contact = await _contactRepo.GetByPeerIdAsync(envelope.RecipientIdentityId, envelope.SenderIdentityId, ct);
        if (contact != null && contact.TrustLevel == PeerTrustLevel.Blocked)
        {
            return DomainResult.Failure(new DomainError("CONTACT_BLOCKED", "Sender is blocked."));
        }

        if (contact == null || contact.State == ContactState.PendingApproval)
        {
            await _pendingHandshakeRepo.SavePendingHandshakeAsync(
                envelope.RecipientIdentityId, envelope.SenderIdentityId, envelope, ct);

            var coordResult = await _contactCoordinator.HandleInboundRequestAsync(
                envelope.RecipientIdentityId,
                envelope.SenderIdentityId,
                envelope.SenderIdentityKey,
                proposedNickname: envelope.SenderIdentityId.ToString(),
                ct);

            if (!coordResult.IsSuccess)
            {
                return DomainResult.Failure(coordResult.Error!);
            }

            return DomainResult.Success();
        }

        // Active contact (Tofu or Verified) -> derive session
        return await ProcessActiveInvitationAsync(envelope, ct);
    }

    public async ValueTask<DomainResult> CompletePendingHandshakeAsync(
        PublicIdentityId recipientId,
        PublicIdentityId senderId,
        CancellationToken ct = default)
    {
        var envelope = await _pendingHandshakeRepo.GetPendingHandshakeAsync(recipientId, senderId, ct);
        if (envelope == null)
        {
            return DomainResult.Failure(new DomainError(
                "PENDING_HANDSHAKE_NOT_FOUND", $"No pending handshake envelope found for sender '{senderId}'."));
        }

        var result = await ProcessActiveInvitationAsync(envelope, ct);
        if (result.IsSuccess)
        {
            await _pendingHandshakeRepo.DeletePendingHandshakeAsync(recipientId, senderId, ct);
        }

        return result;
    }

    private async ValueTask<DomainResult> ProcessActiveInvitationAsync(
        InboundHandshakeEnvelope envelope,
        CancellationToken ct)
    {
        var signedPreKeyPriv = await _preKeyStore.GetSignedPreKeyPrivateAsync(
            envelope.RecipientIdentityId,
            DeviceId.Primary,
            new SignedPreKeyId(envelope.SignedPreKeyId),
            ct);

        if (signedPreKeyPriv == null)
        {
            return DomainResult.Failure(new DomainError("SIGNED_PREKEY_MISSING", "Signed pre-key private key is missing."));
        }

        EphemeralPrivateKey? oneTimeKeyPriv = null;
        if (envelope.OneTimePreKeyId.HasValue)
        {
            oneTimeKeyPriv = await _preKeyStore.TryConsumeOneTimePreKeyPrivateAsync(
                envelope.RecipientIdentityId, DeviceId.Primary, envelope.OneTimePreKeyId.Value, ct);
        }

        using var receiverIdentityPriv = await _identityKeyStore.GetIdentityPrivateKeyAsync(envelope.RecipientIdentityId, ct);
        if (receiverIdentityPriv == null)
        {
            return DomainResult.Failure(new DomainError("IDENTITY_KEY_MISSING", "Receiver identity private key is missing."));
        }

        var opkSpan = oneTimeKeyPriv != null ? oneTimeKeyPriv.Span : ReadOnlySpan<byte>.Empty;

        var masterSecretResult = X3dhAgreement.Receive(
            receiverIdentityPriv.Span,
            signedPreKeyPriv.Span,
            opkSpan,
            envelope.SenderIdentityKey,
            envelope.SenderEphemeralKey,
            _cryptoEngine);

        signedPreKeyPriv.Dispose();
        oneTimeKeyPriv?.Dispose();

        if (!masterSecretResult.IsSuccess)
        {
            return DomainResult.Failure(masterSecretResult.Error!);
        }

        using var masterSecret = masterSecretResult.Value!;
        var sessionResult = DirectRatchetSession.CreateFromX3dhResponder(
            envelope.RecipientIdentityId,
            DeviceId.Primary,
            envelope.SenderIdentityId,
            envelope.SenderDeviceId,
            masterSecret,
            envelope.SenderEphemeralKey,
            _cryptoEngine);

        if (!sessionResult.IsSuccess)
        {
            return DomainResult.Failure(sessionResult.Error!);
        }

        var session = sessionResult.Value!;

        if (envelope.EncryptedPayload.Length > 0)
        {
            var stepResult = session.StepReceivingChain(_cryptoEngine, 0);
            if (!stepResult.IsSuccess)
            {
                return DomainResult.Failure(stepResult.Error!);
            }

            var (counter, messageKey) = stepResult.Value;
            byte[] decrypted;
            try
            {
                using var expanded = messageKey.Expand(counter);
                decrypted = _cryptoEngine.DecryptAesGcm(
                    expanded.CipherKey,
                    expanded.Nonce,
                    envelope.EncryptedPayload.Span,
                    ReadOnlySpan<byte>.Empty);
            }
            catch (Exception ex)
            {
                messageKey.Dispose();
                return DomainResult.Failure(new DomainError("HANDSHAKE_DECRYPT_FAILED", ex.Message));
            }
            finally
            {
                messageKey.Dispose();
            }

            if (decrypted.Length > 0)
            {
                byte[] unpadded;
                try
                {
                    unpadded = PayloadPadding.RemovePadding(decrypted).ToArray();
                }
                catch (Exception)
                {
                    unpadded = (byte[])decrypted.Clone();
                }

                try
                {
                    if (unpadded.Length > 0)
                    {
                        var appId = new AppId(unpadded[0]);
                        var appPayload = unpadded.AsMemory(1);
                        var handlerResult = _appRouter.Resolve(appId);
                        if (handlerResult.IsSuccess)
                        {
                            var channelId = ChannelId.DeterministicForDirect(envelope.RecipientIdentityId, envelope.SenderIdentityId);
                            var appContext = new InboundPayloadContext(
                                channelId,
                                envelope.RecipientIdentityId,
                                envelope.SenderIdentityId,
                                envelope.SenderDeviceId,
                                appId,
                                appPayload,
                                envelope.ReceivedAtUtc);

                            var handleResult = await handlerResult.Value!.HandleInboundAsync(appContext, ct);
                            if (handleResult.IsFailure)
                            {
                                return handleResult;
                            }
                        }
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(decrypted);
                    CryptographicOperations.ZeroMemory(unpadded);
                }
            }
        }

        await _sessionRepo.SaveSessionAsync(session, ct);
        return DomainResult.Success();
    }
}
