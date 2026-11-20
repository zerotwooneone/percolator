namespace Percolator.Infrastructure.Tests.Security;

using System;
using System.Security.Cryptography;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Security;

[TestFixture]
public sealed class ZkgroupCryptographyServiceTests
{
    private readonly ZkgroupCryptographyService _service = new();

    [Test]
    public void VerifyGroupPresentation_ValidProof_ReturnsSuccess()
    {
        uint epoch = 42;
        byte[] challenge = "random-group-transcript-challenge-data"u8.ToArray();
        byte[] groupMasterSecret = new byte[32];
        RandomNumberGenerator.Fill(groupMasterSecret);
        byte[] authCredentialMac = new byte[32];
        RandomNumberGenerator.Fill(authCredentialMac);

        var presentation = _service.GenerateGroupPresentation(
            epoch,
            challenge,
            groupMasterSecret,
            authCredentialMac);

        var publicParams = ZkGroupPublicParams.FromSpan([0x01, 0x02, 0x03]);

        bool isValid = _service.VerifyGroupPresentation(
            epoch,
            presentation,
            challenge,
            publicParams);

        isValid.Should().BeTrue();
    }

    [Test]
    public void VerifyGroupPresentation_WrongEpochOrTamperedChallenge_ReturnsFalse()
    {
        uint epoch = 10;
        byte[] challenge1 = "challenge-alpha"u8.ToArray();
        byte[] challenge2 = "challenge-beta"u8.ToArray();
        byte[] groupMasterSecret = new byte[32];
        RandomNumberGenerator.Fill(groupMasterSecret);
        byte[] authCredentialMac = new byte[32];
        RandomNumberGenerator.Fill(authCredentialMac);

        var presentation = _service.GenerateGroupPresentation(
            epoch,
            challenge1,
            groupMasterSecret,
            authCredentialMac);

        var publicParams = ZkGroupPublicParams.FromSpan([0x01, 0x02, 0x03]);

        // Wrong epoch
        _service.VerifyGroupPresentation(
            epoch + 1,
            presentation,
            challenge1,
            publicParams).Should().BeFalse();

        // Tampered challenge
        _service.VerifyGroupPresentation(
            epoch,
            presentation,
            challenge2,
            publicParams).Should().BeFalse();
    }

    [Test]
    public void VerifyGroupPresentation_ForgedProofTag_ReturnsFalse()
    {
        uint epoch = 15;
        byte[] challenge = "challenge-integrity"u8.ToArray();
        byte[] groupMasterSecret = new byte[32];
        RandomNumberGenerator.Fill(groupMasterSecret);
        byte[] authCredentialMac = new byte[32];
        RandomNumberGenerator.Fill(authCredentialMac);

        var presentation = _service.GenerateGroupPresentation(
            epoch,
            challenge,
            groupMasterSecret,
            authCredentialMac);

        var publicParams = ZkGroupPublicParams.FromSpan([0x01, 0x02, 0x03]);

        // Mutate signature byte in presentation proof tag
        byte[] forged = presentation.Span.ToArray();
        forged[80] ^= 0xFF;

        _service.VerifyGroupPresentation(
            epoch,
            ZkPresentationBytes.FromSpan(forged),
            challenge,
            publicParams).Should().BeFalse();
    }

    [Test]
    public void VerifyGroupPresentation_ZeroProofTag_ReturnsFalse()
    {
        uint epoch = 20;
        byte[] challenge = "challenge-zero-tag"u8.ToArray();
        byte[] groupMasterSecret = new byte[32];
        RandomNumberGenerator.Fill(groupMasterSecret);
        byte[] authCredentialMac = new byte[32];
        RandomNumberGenerator.Fill(authCredentialMac);

        var presentation = _service.GenerateGroupPresentation(
            epoch,
            challenge,
            groupMasterSecret,
            authCredentialMac);

        var publicParams = ZkGroupPublicParams.FromSpan([0x01, 0x02, 0x03]);

        // Zero out public key and signature tag
        byte[] zeroed = presentation.Span.ToArray();
        Array.Clear(zeroed, 40, zeroed.Length - 40);

        _service.VerifyGroupPresentation(
            epoch,
            ZkPresentationBytes.FromSpan(zeroed),
            challenge,
            publicParams).Should().BeFalse();
    }

    [Test]
    public void ZkGroupPublicParams_EmptyBytes_ThrowsArgumentException()
    {
        var act = () => ZkGroupPublicParams.FromSpan([]);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*length in range*");
    }

    [Test]
    public void VerifyGroupPresentation_InvalidMagicOrLength_ReturnsFalse()
    {
        uint epoch = 7;
        byte[] challenge = "challenge-magic"u8.ToArray();
        byte[] groupMasterSecret = new byte[32];
        RandomNumberGenerator.Fill(groupMasterSecret);
        byte[] authCredentialMac = new byte[32];
        RandomNumberGenerator.Fill(authCredentialMac);

        var presentation = _service.GenerateGroupPresentation(
            epoch,
            challenge,
            groupMasterSecret,
            authCredentialMac);

        var publicParams = ZkGroupPublicParams.FromSpan([0x01, 0x02]);

        // Corrupt magic
        byte[] corruptMagic = presentation.Span.ToArray();
        corruptMagic[0] ^= 0xAA;
        _service.VerifyGroupPresentation(
            epoch,
            ZkPresentationBytes.FromSpan(corruptMagic),
            challenge,
            publicParams).Should().BeFalse();

        // Too short
        _service.VerifyGroupPresentation(
            epoch,
            ZkPresentationBytes.FromSpan(new byte[71]),
            challenge,
            publicParams).Should().BeFalse();
    }
}
