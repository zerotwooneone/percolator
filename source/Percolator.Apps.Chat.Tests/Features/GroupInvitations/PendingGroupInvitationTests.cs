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
    private static readonly InvitationId TestInvitationId = new("inv-1");
    private const ulong ExpirationMs = 1_000_000;

    [Test]
    public void Accept_WhenPendingAndNotExpired_TransitionsToAccepted()
    {
        var invitation = new PendingGroupInvitation(
            TestInvitationId,
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
            TestInvitationId,
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
            TestInvitationId,
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
            TestInvitationId,
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
            TestInvitationId,
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

    [Test]
    public void InvitationId_Create_ValidString_Succeeds()
    {
        var result = InvitationId.Create("valid-invite-id-123");

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be("valid-invite-id-123");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void InvitationId_Create_NullOrWhitespace_Fails(string? invalid)
    {
        var result = InvitationId.Create(invalid!);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_INVITATION_ID");
    }

    [Test]
    public void InvitationId_Create_Exceeds128Chars_Fails()
    {
        var longId = new string('x', 129);
        var result = InvitationId.Create(longId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_INVITATION_ID");
    }
}
