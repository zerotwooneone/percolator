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
}
