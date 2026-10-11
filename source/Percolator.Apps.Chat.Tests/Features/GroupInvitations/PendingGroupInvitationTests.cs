using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.Chat.Features.GroupInvitations;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Tests.Features.GroupInvitations;

[TestFixture]
public class PendingGroupInvitationTests
{
    private static readonly ChannelId GroupChannelId = ChannelId.New();
    private static readonly PublicIdentityId Inviter = PublicIdentityId.New();
    private const ulong ExpirationMs = 1_000_000;

    [Test]
    public void Accept_WhenPendingAndNotExpired_TransitionsToAccepted()
    {
        var invitation = new PendingGroupInvitation(
            "inv-1",
            GroupChannelId,
            "Secret Agents",
            Inviter,
            null,
            ExpirationMs);

        var result = invitation.Accept(timestampUnixMs: 500_000);

        result.IsSuccess.Should().BeTrue();
        invitation.Status.Should().Be(GroupInvitationStatus.Accepted);
        invitation.RespondedAtUnixMs.Should().Be(500_000);
    }

    [Test]
    public void Decline_WhenPendingAndNotExpired_TransitionsToDeclined()
    {
        var invitation = new PendingGroupInvitation(
            "inv-1",
            GroupChannelId,
            "Secret Agents",
            Inviter,
            null,
            ExpirationMs);

        var result = invitation.Decline(timestampUnixMs: 500_000);

        result.IsSuccess.Should().BeTrue();
        invitation.Status.Should().Be(GroupInvitationStatus.Declined);
        invitation.RespondedAtUnixMs.Should().Be(500_000);
    }

    [Test]
    public void Accept_WhenExpired_FailsWithInvitationExpiredError_AndTransitionsToExpired()
    {
        var invitation = new PendingGroupInvitation(
            "inv-1",
            GroupChannelId,
            "Secret Agents",
            Inviter,
            null,
            ExpirationMs);

        var result = invitation.Accept(timestampUnixMs: ExpirationMs + 10);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVITATION_EXPIRED");
        invitation.Status.Should().Be(GroupInvitationStatus.Expired);
    }

    [Test]
    public void Decline_WhenExpired_FailsWithInvitationExpiredError_AndTransitionsToExpired()
    {
        var invitation = new PendingGroupInvitation(
            "inv-1",
            GroupChannelId,
            "Secret Agents",
            Inviter,
            null,
            ExpirationMs);

        var result = invitation.Decline(timestampUnixMs: ExpirationMs + 10);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVITATION_EXPIRED");
        invitation.Status.Should().Be(GroupInvitationStatus.Expired);
    }

    [TestCase(GroupInvitationStatus.Accepted)]
    [TestCase(GroupInvitationStatus.Declined)]
    [TestCase(GroupInvitationStatus.Expired)]
    public void AcceptOrDecline_WhenNotPending_FailsWithInvalidStateTransition(GroupInvitationStatus nonPendingStatus)
    {
        var invitation = new PendingGroupInvitation(
            "inv-1",
            GroupChannelId,
            "Secret Agents",
            Inviter,
            null,
            ExpirationMs,
            status: nonPendingStatus);

        var acceptResult = invitation.Accept(timestampUnixMs: 500_000);
        acceptResult.IsFailure.Should().BeTrue();
        acceptResult.Error.Code.Should().Be("INVALID_STATE_TRANSITION");

        var declineResult = invitation.Decline(timestampUnixMs: 500_000);
        declineResult.IsFailure.Should().BeTrue();
        declineResult.Error.Code.Should().Be("INVALID_STATE_TRANSITION");
    }
}
