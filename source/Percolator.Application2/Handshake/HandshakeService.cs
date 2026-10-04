using System.Security.Cryptography;
using Percolator.Application2.Delivery;
using Percolator.Application2.Delivery.Ports;
using Percolator.Application2.Ingress;
using Percolator.Application2.Ports;
using Percolator.Application2.Profiles;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.PluginSdk;
using DeliveryRoute = Percolator.Application2.Delivery.DeliveryRoute;

namespace Percolator.Application2.Handshake;

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
        IAppRouter appRouter)
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
    }

    public async ValueTask<DomainResult<DirectRatchetSession>> InitiateHandshakeAsync(
        PublicIdentityId ownerId,
        DeviceId ownerDeviceId,
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

        var privateKeyBytes = await _identityKeyStore.GetIdentityPrivateKeyAsync(ownerId, ct);
        if (privateKeyBytes == null || privateKeyBytes.Length == 0)
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

        var x3dhResult = X3dhAgreement.Initiate(privateKeyBytes, ownerPublicKey, remoteBundle, _cryptoEngine);
        if (!x3dhResult.IsSuccess)
        {
            return DomainResult<DirectRatchetSession>.Failure(x3dhResult.Error!);
        }

        var initiatorResult = x3dhResult.Value!;
        var sessionResult = DirectRatchetSession.CreateFromX3dhInitiator(
            ownerId,
            ownerDeviceId,
            remoteBundle.IdentityId,
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

            var (_, messageKey, _) = stepResult.Value;
            var inner = new byte[1 + initialPayload.Value.Length];
            inner[0] = initialAppId.Value.Value;
            initialPayload.Value.Span.CopyTo(inner.AsSpan(1));

            var nonce = new byte[12];
            encryptedInitialPayload = _cryptoEngine.EncryptAesGcm(messageKey.Span, nonce, inner, ReadOnlySpan<byte>.Empty);
            messageKey.Dispose();
        }

        await _sessionRepo.SaveSessionAsync(session, ct);

        // Assemble handshake outbox package
        var handshakePackage = new byte[32 + (encryptedInitialPayload?.Length ?? 0)];
        initiatorResult.EphemeralPublicKey.Span.CopyTo(handshakePackage.AsSpan(0, 32));
        if (encryptedInitialPayload != null)
        {
            Buffer.BlockCopy(encryptedInitialPayload, 0, handshakePackage, 32, encryptedInitialPayload.Length);
        }

        var route = new DeliveryRoute(DeliveryRouteType.DirectP2P, null, null);
        var jobResult = OutboxJob.Create(
            ChannelId.New(),
            ownerId,
            remoteBundle.IdentityId,
            route,
            handshakePackage,
            _timeProvider);

        if (jobResult.IsSuccess)
        {
            await _outboxRepo.SaveAsync(jobResult.Value!, ct);
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
            if (envelope.EncryptedPayload.Length > 0)
            {
                await _pendingHandshakeRepo.SavePendingHandshakeAsync(
                    envelope.RecipientIdentityId, envelope.SenderIdentityId, envelope, ct);
            }

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
            return DomainResult.Success();
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
            envelope.RecipientIdentityId, DeviceId.Primary, ct);

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

        var receiverIdentityPriv = await _identityKeyStore.GetIdentityPrivateKeyAsync(envelope.RecipientIdentityId, ct);
        if (receiverIdentityPriv == null || receiverIdentityPriv.Length == 0)
        {
            return DomainResult.Failure(new DomainError("IDENTITY_KEY_MISSING", "Receiver identity private key is missing."));
        }

        var opkSpan = oneTimeKeyPriv != null ? oneTimeKeyPriv.Span : ReadOnlySpan<byte>.Empty;

        var masterSecretResult = X3dhAgreement.Receive(
            receiverIdentityPriv,
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

            var (_, messageKey) = stepResult.Value;
            var nonce = new byte[12];
            byte[] decrypted;
            try
            {
                decrypted = _cryptoEngine.DecryptAesGcm(
                    messageKey.Span,
                    nonce,
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
                var appId = new AppId(decrypted[0]);
                var appPayload = decrypted.AsMemory(1);
                var handlerResult = _appRouter.Resolve(appId);
                if (handlerResult.IsSuccess)
                {
                    var appContext = new InboundPayloadContext(
                        ChannelId.New(),
                        envelope.SenderIdentityId,
                        envelope.SenderDeviceId,
                        appId,
                        appPayload,
                        envelope.ReceivedAtUtc);

                    await handlerResult.Value!.HandleInboundAsync(appContext, ct);
                }

                Array.Clear(decrypted, 0, decrypted.Length);
            }
        }

        await _sessionRepo.SaveSessionAsync(session, ct);
        return DomainResult.Success();
    }
}
