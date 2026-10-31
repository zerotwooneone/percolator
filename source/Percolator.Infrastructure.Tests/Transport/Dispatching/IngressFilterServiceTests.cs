namespace Percolator.Infrastructure.Tests.Transport.Dispatching;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Transport;

[TestFixture]
public sealed class IngressFilterServiceTests
{
    private sealed class InMemoryContactRepository : IPeerContactRepository
    {
        private readonly Dictionary<(PublicIdentityId, PublicIdentityId), PeerContact> _contacts = new();

        public void AddContact(PeerContact contact)
        {
            _contacts[(contact.OwnerIdentityId, contact.RemotePeerId)] = contact;
        }

        public Task<PeerContact?> GetByPeerIdAsync(PublicIdentityId ownerId, PublicIdentityId remotePeerId, CancellationToken cancellationToken = default)
        {
            _contacts.TryGetValue((ownerId, remotePeerId), out var contact);
            return Task.FromResult(contact);
        }

        public Task<IReadOnlyList<PeerContact>> GetAllForOwnerAsync(PublicIdentityId ownerId, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<PeerContact> list = new List<PeerContact>(_contacts.Values);
            return Task.FromResult(list);
        }

        public Task SaveAsync(PeerContact contact, CancellationToken cancellationToken = default)
        {
            _contacts[(contact.OwnerIdentityId, contact.RemotePeerId)] = contact;
            return Task.CompletedTask;
        }
    }

    [Test]
    public async Task BlockedSender_RejectsEnvelopeBeforeCrypto()
    {
        var repo = new InMemoryContactRepository();
        var recipientId = PublicIdentityId.New();
        var blockedSenderId = PublicIdentityId.New();
        var allowedSenderId = PublicIdentityId.New();

        var blockedContact = new PeerContact(
            recipientId,
            blockedSenderId,
            ContactNickname.Create("BlockedUser", blockedSenderId),
            PeerTrustLevel.Blocked,
            DateTimeOffset.UtcNow);
        repo.AddContact(blockedContact);

        var allowedContact = new PeerContact(
            recipientId,
            allowedSenderId,
            ContactNickname.Create("AllowedUser", allowedSenderId),
            PeerTrustLevel.Verified,
            DateTimeOffset.UtcNow);
        repo.AddContact(allowedContact);

        var service = new IngressFilterService(repo);

        // Blocked sender rejection
        var blockedResult = await service.CheckIngressAllowedAsync(recipientId, blockedSenderId);
        blockedResult.IsFailure.Should().BeTrue();
        blockedResult.Error!.Code.Should().Be("BLOCKED_SENDER");

        // Allowed sender success
        var allowedResult = await service.CheckIngressAllowedAsync(recipientId, allowedSenderId);
        allowedResult.IsSuccess.Should().BeTrue();

        // Unknown sender (not in contacts) success by default
        var unknownSenderId = PublicIdentityId.New();
        var unknownResult = await service.CheckIngressAllowedAsync(recipientId, unknownSenderId);
        unknownResult.IsSuccess.Should().BeTrue();
    }
}
