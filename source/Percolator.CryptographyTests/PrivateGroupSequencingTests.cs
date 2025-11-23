using System;
using System.Security.Cryptography;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Cryptography;

namespace Percolator.CryptographyTests;

[TestFixture]
public class PrivateGroupSequencingTests
{
    [Test]
    public void Recipient_Reapplies_Same_AddMember_Change_Should_Throw()
    {
        using var admin = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var creator = PrivateGroup.CreateGroup();
        using var bob = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bobSpki = bob.ExportSubjectPublicKeyInfo();

        var change = creator.CreateAddMemberChange(bobSpki, admin);

        using var adminVerify = ECDsa.Create();
        adminVerify.ImportSubjectPublicKeyInfo(admin.ExportSubjectPublicKeyInfo(), out _);

        var recipient = PrivateGroup.AcceptInvite(
            creator.CreateInvitePayload(admin),
            adminVerify);

        recipient.ApplyAddMemberChange(change, adminVerify);
        recipient.SequenceNumber.Should().Be(1UL);
        Action again = () => recipient.ApplyAddMemberChange(change, adminVerify);
        again.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void Recipient_Accepts_Exactly_CurrentPlus1_Changes()
    {
        using var admin = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var creator = PrivateGroup.CreateGroup();
        using var alice = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var bob = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var aliceSpki = alice.ExportSubjectPublicKeyInfo();
        var bobSpki = bob.ExportSubjectPublicKeyInfo();

        var addAlice = creator.CreateAddMemberChange(aliceSpki, admin); // seq 1
        var addBob = creator.CreateAddMemberChange(bobSpki, admin);     // seq 2

        using var adminVerify = ECDsa.Create();
        adminVerify.ImportSubjectPublicKeyInfo(admin.ExportSubjectPublicKeyInfo(), out _);

        var recipient = PrivateGroup.AcceptInvite(
            creator.CreateInvitePayload(admin),
            adminVerify);

        recipient.SequenceNumber.Should().Be(0UL);
        recipient.ApplyAddMemberChange(addAlice, adminVerify);
        recipient.SequenceNumber.Should().Be(1UL);
        recipient.ApplyAddMemberChange(addBob, adminVerify);
        recipient.SequenceNumber.Should().Be(2UL);
    }
}
