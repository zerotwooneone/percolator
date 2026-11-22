using Percolator.Domain.Common;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk.Events;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Application.Profiles;

public interface IContactRequestCoordinator
{
    ValueTask<DomainResult<PeerContact>> HandleInboundRequestAsync(
        PublicIdentityId ownerId,
        PublicIdentityId remotePeerId,
        IdentityKey peerPublicKey,
        string proposedNickname,
        CancellationToken ct = default);

    ValueTask<DomainResult> ApproveRequestAsync(
        PublicIdentityId ownerId,
        PublicIdentityId remotePeerId,
        PeerTrustLevel initialTrust = PeerTrustLevel.Tofu,
        CancellationToken ct = default);

    ValueTask<DomainResult> RejectRequestAsync(
        PublicIdentityId ownerId,
        PublicIdentityId remotePeerId,
        bool block = false,
        CancellationToken ct = default);
}

public sealed class ContactRequestCoordinator : IContactRequestCoordinator
{
    private readonly IPeerContactRepository _contactRepo;
    private readonly IDateTimeProvider _timeProvider;
    private readonly IEventPublisher _eventPublisher;

    public ContactRequestCoordinator(
        IPeerContactRepository contactRepo,
        IDateTimeProvider timeProvider,
        IEventPublisher eventPublisher)
    {
        _contactRepo = contactRepo ?? throw new ArgumentNullException(nameof(contactRepo));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
    }

    public async ValueTask<DomainResult<PeerContact>> HandleInboundRequestAsync(
        PublicIdentityId ownerId,
        PublicIdentityId remotePeerId,
        IdentityKey peerPublicKey,
        string proposedNickname,
        CancellationToken ct = default)
    {
        var existing = await _contactRepo.GetByPeerIdAsync(ownerId, remotePeerId, ct);
        if (existing != null)
        {
            if (existing.TrustLevel == PeerTrustLevel.Blocked)
            {
                return DomainResult<PeerContact>.Failure(new DomainError(
                    "CONTACT_BLOCKED", "Inbound request from blocked peer was rejected."));
            }

            if (existing.State == ContactState.Rejected)
            {
                var reopenResult = existing.ReopenInboundRequest(peerPublicKey, proposedNickname, _timeProvider);
                if (!reopenResult.IsSuccess)
                {
                    return DomainResult<PeerContact>.Failure(reopenResult.Error);
                }

                await _contactRepo.SaveAsync(existing, ct);
                await PublishAndClearDomainEventsAsync(existing, ct);
                return DomainResult<PeerContact>.Success(existing);
            }

            return DomainResult<PeerContact>.Success(existing);
        }

        var contactResult = PeerContact.CreateInboundRequest(
            ownerId,
            remotePeerId,
            peerPublicKey,
            proposedNickname,
            _timeProvider);

        if (!contactResult.IsSuccess)
        {
            return contactResult;
        }

        var contact = contactResult.Value!;
        await _contactRepo.SaveAsync(contact, ct);
        await PublishAndClearDomainEventsAsync(contact, ct);
        return DomainResult<PeerContact>.Success(contact);
    }

    public async ValueTask<DomainResult> ApproveRequestAsync(
        PublicIdentityId ownerId,
        PublicIdentityId remotePeerId,
        PeerTrustLevel initialTrust = PeerTrustLevel.Tofu,
        CancellationToken ct = default)
    {
        var contact = await _contactRepo.GetByPeerIdAsync(ownerId, remotePeerId, ct);
        if (contact == null)
        {
            return DomainResult.Failure(new DomainError("CONTACT_NOT_FOUND", "No contact found to approve."));
        }

        var approveResult = contact.Approve(initialTrust, _timeProvider);
        if (!approveResult.IsSuccess)
        {
            return approveResult;
        }

        await _contactRepo.SaveAsync(contact, ct);
        await PublishAndClearDomainEventsAsync(contact, ct);
        return DomainResult.Success();
    }

    public async ValueTask<DomainResult> RejectRequestAsync(
        PublicIdentityId ownerId,
        PublicIdentityId remotePeerId,
        bool block = false,
        CancellationToken ct = default)
    {
        var contact = await _contactRepo.GetByPeerIdAsync(ownerId, remotePeerId, ct);
        if (contact == null)
        {
            return DomainResult.Failure(new DomainError("CONTACT_NOT_FOUND", "No contact found to reject."));
        }

        var rejectResult = contact.Reject(block, _timeProvider);
        if (!rejectResult.IsSuccess)
        {
            return rejectResult;
        }

        await _contactRepo.SaveAsync(contact, ct);
        await PublishAndClearDomainEventsAsync(contact, ct);
        return DomainResult.Success();
    }

    private async ValueTask PublishAndClearDomainEventsAsync(PeerContact contact, CancellationToken ct)
    {
        try
        {
            foreach (var domainEvt in contact.DomainEvents)
            {
                var appEvt = domainEvt.ToApplicationEvent();
                if (appEvt != null)
                {
                    await _eventPublisher.PublishAsync(appEvt, ct);
                }
            }
        }
        finally
        {
            contact.ClearDomainEvents();
        }
    }
}
