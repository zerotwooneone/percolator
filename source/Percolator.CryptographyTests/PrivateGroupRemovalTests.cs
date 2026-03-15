using System.Security.Cryptography;
using FluentAssertions;
using Percolator.Cryptography;

namespace Percolator.CryptographyTests;

[TestFixture]
public class PrivateGroupRemovalTests
{
    [Test]
    public void Admin_Emits_RemoveMember_Change_Payload()
    {
        using var creatorSign = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var group = PrivateGroup.CreateGroup();

        using var member = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var memberSpki = member.ExportSubjectPublicKeyInfo();
        var change = group.CreateRemoveMemberChange(memberSpki, creatorSign);

        change.Should().NotBeNull();
        change.Length.Should().BeGreaterThan(0);
    }

    [Test]
    public void Recipient_Applies_RemoveMember_And_SenderKey_Rotation()
    {
        using var creatorSign = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var creatorGroup = PrivateGroup.CreateGroup();
        using var bob = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bobSpki = bob.ExportSubjectPublicKeyInfo();

        // Creator invites and then removes Bob
        var invite = creatorGroup.CreateInvitePayload(creatorSign);
        var removeChange = creatorGroup.CreateRemoveMemberChange(bobSpki, creatorSign);
        var newSenderKey = RandomNumberGenerator.GetBytes(32);
        var rotation = creatorGroup.CreateSenderKeyRotationPayload(newSenderKey, creatorSign);

        using var creatorVerify = ECDsa.Create();
        creatorVerify.ImportSubjectPublicKeyInfo(creatorSign.ExportSubjectPublicKeyInfo(), out _);

        var recipientGroup = PrivateGroup.AcceptInvite(invite, creatorVerify);
        recipientGroup.ApplyRemoveMemberChange(removeChange, creatorVerify);
        recipientGroup.SequenceNumber.Should().Be(1UL);
        recipientGroup.HasMember(bobSpki).Should().BeFalse();

        recipientGroup.ApplySenderKeyRotationPayload(rotation, creatorVerify);
        recipientGroup.GetCurrentSenderKey().Should().BeEquivalentTo(newSenderKey);
    }
}
