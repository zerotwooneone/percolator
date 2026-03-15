using System.Security.Cryptography;
using FluentAssertions;
using Percolator.Cryptography;

namespace Percolator.CryptographyTests;

[TestFixture]
public class PrivateGroupValidationTests
{
    [Test]
    public void ApplyAddMember_WithInvalidSignature_Throws()
    {
        using var admin = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var member = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = member.ExportSubjectPublicKeyInfo();
        var group = PrivateGroup.CreateGroup();

        // Create a valid change with admin, then corrupt its signature bytes
        var valid = group.CreateAddMemberChange(spki, admin);
        // Flip a bit in the signature tail
        valid[^1] ^= 0xFF;

        using var adminVerify = ECDsa.Create();
        adminVerify.ImportSubjectPublicKeyInfo(admin.ExportSubjectPublicKeyInfo(), out _);

        var recipient = PrivateGroup.AcceptInvite(group.CreateInvitePayload(admin), adminVerify);
        Action act = () => recipient.ApplyAddMemberChange(valid, adminVerify);
        act.Should().Throw<CryptographicException>();
    }

    [Test]
    public void ApplyRemoveMember_WithWrongGroupId_Throws()
    {
        using var admin = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var member = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = member.ExportSubjectPublicKeyInfo();
        var groupA = PrivateGroup.CreateGroup();
        var groupB = PrivateGroup.CreateGroup();

        var changeForA = groupA.CreateRemoveMemberChange(spki, admin);

        using var adminVerify = ECDsa.Create();
        adminVerify.ImportSubjectPublicKeyInfo(admin.ExportSubjectPublicKeyInfo(), out _);
        var recipientB = PrivateGroup.AcceptInvite(groupB.CreateInvitePayload(admin), adminVerify);

        Action act = () => recipientB.ApplyRemoveMemberChange(changeForA, adminVerify);
        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void GetCurrentSenderKey_WithoutRotation_Throws()
    {
        using var admin = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var group = PrivateGroup.CreateGroup();
        Action act = () => group.GetCurrentSenderKey();
        act.Should().Throw<InvalidOperationException>();
    }
}
