using System.Security.Cryptography;
using FluentAssertions;
using Percolator.Cryptography;

namespace Percolator.CryptographyTests;

[TestFixture]
public class PrivateGroupGenesisTests
{
    [Test]
    public void Creator_Creates_Group_And_Produces_Invite_Payload_For_Member()
    {
        using var creatorSign = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var group = PrivateGroup.CreateGroup();

        // Produce an invite payload destined for a member (transport is 1:1 SecureSession in real flow)
        var memberIdentitySpki = ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportSubjectPublicKeyInfo();
        var invite = group.CreateInvitePayload(creatorSign);

        invite.Should().NotBeNull();
        invite.Length.Should().BeGreaterThan(0);
    }

    [Test]
    public void Member_Accepts_Invite_And_Initializes_Local_Group_State()
    {
        using var creatorSign = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var creatorGroup = PrivateGroup.CreateGroup();

        // Creator makes an invite payload for Bob
        using var bobSign = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bobIdentitySpki = bobSign.ExportSubjectPublicKeyInfo();
        var invite = creatorGroup.CreateInvitePayload(creatorSign);

        // Bob imports creator pubkey to verify invite
        using var creatorVerify = ECDsa.Create();
        creatorVerify.ImportSubjectPublicKeyInfo(creatorSign.ExportSubjectPublicKeyInfo(), out _);

        var bobGroup = PrivateGroup.AcceptInvite(invite, creatorVerify);
        bobGroup.Should().NotBeNull();
        bobGroup.GroupId.Should().Be(creatorGroup.GroupId);
        bobGroup.SequenceNumber.Should().Be(0UL);
    }
}
