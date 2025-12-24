using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using MediatR;
using Percolator.Application.Network;
using Percolator.Application.ReverseSignal;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;

namespace Percolator.Application.Ingress;

public sealed class PendingHandshakeIngress : IPendingHandshakeIngress
{
    private readonly IIngressReadinessGate _readiness;
    private readonly ReverseSignalInvitationService _invitations;
    private readonly IClock _clock;
    private readonly IPeerIdentityRepository _peers;
    private readonly IPeerPublicSigningKeyStore _pkhStore;
    private readonly IMediator _mediator;

    public PendingHandshakeIngress(
        IIngressReadinessGate readiness,
        ReverseSignalInvitationService invitations,
        IClock clock,
        IPeerIdentityRepository peers,
        IPeerPublicSigningKeyStore pkhStore,
        IMediator mediator)
    {
        _readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
        _invitations = invitations ?? throw new ArgumentNullException(nameof(invitations));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _peers = peers ?? throw new ArgumentNullException(nameof(peers));
        _pkhStore = pkhStore ?? throw new ArgumentNullException(nameof(pkhStore));
        _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
    }

    public async Task<PendingHandshakeIngressResult> CreateFromInitiatorHelloAsync(
        byte[] initiatorHelloBytes,
        string? displayName = null,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _readiness.EnsureReady();
        }
        catch (Exception ex)
        {
            return new PendingHandshakeIngressResult(
                PendingHandshakeIngressStatus.RejectedNotReady,
                PendingSessionId: null,
                NotUntil: null,
                ErrorMessage: ex.Message);
        }

        if (initiatorHelloBytes is null || initiatorHelloBytes.Length == 0)
        {
            return new PendingHandshakeIngressResult(
                PendingHandshakeIngressStatus.RejectedInvalid,
                PendingSessionId: null,
                NotUntil: null,
                ErrorMessage: "initiator hello bytes required");
        }

        HandshakeInitiatorHello hello;
        try
        {
            hello = HandshakeInitiatorHello.Parser.ParseFrom(initiatorHelloBytes);
        }
        catch (Exception ex)
        {
            return new PendingHandshakeIngressResult(
                PendingHandshakeIngressStatus.RejectedInvalid,
                PendingSessionId: null,
                NotUntil: null,
                ErrorMessage: ex.Message);
        }

        if (!hello.HasInitiatorIdentityKeySpki || !hello.HasInitiatorEphemeralKeySpki || !hello.HasSignedPreKeyId)
        {
            return new PendingHandshakeIngressResult(
                PendingHandshakeIngressStatus.RejectedInvalid,
                PendingSessionId: null,
                NotUntil: null,
                ErrorMessage: "initiator hello missing required fields");
        }

        try
        {
            var spki = hello.InitiatorIdentityKeySpki.ToByteArray();
            var pkh = SHA256.HashData(spki);
            var resolvedPeerId = await _pkhStore.GetPeerIdByPublicKeyHashAsync(pkh, cancellationToken).ConfigureAwait(false);

            PeerIdentity identity;
            if (resolvedPeerId is not null)
            {
                identity = await _peers.GetByIdAsync(resolvedPeerId, cancellationToken).ConfigureAwait(false)
                           ?? new PeerIdentity(resolvedPeerId);
            }
            else
            {
                identity = await _peers.FindByPublicKeyHashAsync(pkh, cancellationToken).ConfigureAwait(false)
                           ?? new PeerIdentity(Percolator.Identity.PeerId.NewId());
            }

            if (!string.IsNullOrWhiteSpace(displayName))
            {
                identity.SetDisplayName(new DisplayName(displayName));
            }

            await _peers.SaveAsync(identity, cancellationToken).ConfigureAwait(false);
            await _pkhStore.ActivateIfChangedAsync(identity.Id, spki, pkh, _clock.UtcNow, cancellationToken).ConfigureAwait(false);

            var remotePeerId = new Percolator.Cryptography.Primitives.PeerId(identity.Id.Value);

            var invitation = new HandshakeInvitation(initiatorHelloBytes);
            var id = await _invitations.CreatePendingAsync(
                remotePeerId,
                new ProtocolVersion(1),
                invitation,
                ttl ?? TimeSpan.FromMinutes(10),
                cancellationToken).ConfigureAwait(false);

            await _mediator.Publish(new PendingSessionCreatedNotification(id), cancellationToken).ConfigureAwait(false);

            return new PendingHandshakeIngressResult(
                PendingHandshakeIngressStatus.Accepted,
                PendingSessionId: id,
                NotUntil: null,
                ErrorMessage: null);
        }
        catch (Exception ex)
        {
            return new PendingHandshakeIngressResult(
                PendingHandshakeIngressStatus.Failed,
                PendingSessionId: null,
                NotUntil: null,
                ErrorMessage: ex.Message);
        }
    }
}
