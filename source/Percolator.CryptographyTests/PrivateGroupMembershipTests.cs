using System;
using System.Security.Cryptography;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Cryptography;

namespace Percolator.CryptographyTests;

[TestFixture]
public class PrivateGroupMembershipTests
{
    [Test]
    public void Admin_Emits_AddMember_Change_Payload()
    {
        using var creatorSign = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var group = PrivateGroup.CreateGroup();

        using var newMember = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var memberSpki = newMember.ExportSubjectPublicKeyInfo();

        var change = group.CreateAddMemberChange(memberSpki, creatorSign);
        change.Should().NotBeNull();
        change.Length.Should().BeGreaterThan(0);
    }

    [Test]
    public void Recipient_Applies_AddMember_Change_And_Increments_Sequence()
    {
        using var creatorSign = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var creatorGroup = PrivateGroup.CreateGroup();

        using var newMember = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var memberSpki = newMember.ExportSubjectPublicKeyInfo();
        var change = creatorGroup.CreateAddMemberChange(memberSpki, creatorSign);

        using var creatorVerify = ECDsa.Create();
        creatorVerify.ImportSubjectPublicKeyInfo(creatorSign.ExportSubjectPublicKeyInfo(), out _);

        var recipientGroup = PrivateGroup.AcceptInvite(creatorGroup.CreateInvitePayload(creatorSign), creatorVerify);
        recipientGroup.ApplyAddMemberChange(change, creatorVerify);

        recipientGroup.SequenceNumber.Should().Be(1UL);
        recipientGroup.HasMember(memberSpki).Should().BeTrue();
    }
}
