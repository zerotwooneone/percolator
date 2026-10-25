using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.Discovery.Features.PrivateRendezvous;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Discovery.Tests.PrivateRendezvous;

[TestFixture]
public sealed class PrivateRendezvousTests
{
    private static readonly byte[] SharedSecret = new byte[32]
    {
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
        0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f, 0x10,
        0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18,
        0x19, 0x1a, 0x1b, 0x1c, 0x1d, 0x1e, 0x1f, 0x20
    };

    [Test]
    public void BlindedLocator_Derive_IsDeterministicForSameInputs()
    {
        var peerId = PublicIdentityId.New();
        var epoch = new DiscoveryEpoch(19500);

        var loc1 = BlindedLocator.Derive(peerId, SharedSecret, epoch);
        var loc2 = BlindedLocator.Derive(peerId, SharedSecret, epoch);

        loc1.Should().Be(loc2);
        loc1.Value.Should().Be(loc2.Value);
        loc1.Value.Length.Should().Be(64); // 32 bytes hex
        loc1.Span.Length.Should().Be(BlindedLocator.ByteLength);
    }

    [Test]
    public void BlindedLocator_Derive_DiffersAcrossEpochs()
    {
        var peerId = PublicIdentityId.New();
        var epochDay1 = new DiscoveryEpoch(19500);
        var epochDay2 = new DiscoveryEpoch(19501);

        var locDay1 = BlindedLocator.Derive(peerId, SharedSecret, epochDay1);
        var locDay2 = BlindedLocator.Derive(peerId, SharedSecret, epochDay2);

        locDay1.Should().NotBe(locDay2);
    }

    [Test]
    public void BlindedLocator_Derive_DiffersForDifferentSharedSecrets()
    {
        var peerId = PublicIdentityId.New();
        var epoch = new DiscoveryEpoch(19500);
        var differentSecret = (byte[])SharedSecret.Clone();
        differentSecret[0] ^= 0xFF;

        var loc1 = BlindedLocator.Derive(peerId, SharedSecret, epoch);
        var loc2 = BlindedLocator.Derive(peerId, differentSecret, epoch);

        loc1.Should().NotBe(loc2);
    }

    [Test]
    public void BlindedLocator_DeriveWindow_ReturnsAdjacentEpochsForMidnightClockSkew()
    {
        var peerId = PublicIdentityId.New();
        var currentEpoch = new DiscoveryEpoch(20000);

        var window = BlindedLocator.DeriveWindow(peerId, SharedSecret, currentEpoch);
        window.Should().HaveCount(3);
        window[0].Should().Be(BlindedLocator.Derive(peerId, SharedSecret, new DiscoveryEpoch(19999)));
        window[1].Should().Be(BlindedLocator.Derive(peerId, SharedSecret, new DiscoveryEpoch(20000)));
        window[2].Should().Be(BlindedLocator.Derive(peerId, SharedSecret, new DiscoveryEpoch(20001)));
    }

    [Test]
    public void BlindedLocator_Create_ValidatesHexLengthAndCharacters()
    {
        var validHex = new string('a', 64);
        var validResult = BlindedLocator.Create(validHex);
        validResult.IsSuccess.Should().BeTrue();
        validResult.Value.Value.Should().Be(validHex);

        var invalidLength = BlindedLocator.Create(new string('a', 63));
        invalidLength.IsSuccess.Should().BeFalse();
        invalidLength.Error.Code.Should().Be("INVALID_LOCATOR_LENGTH");

        var invalidChar = BlindedLocator.Create(new string('a', 63) + "z");
        invalidChar.IsSuccess.Should().BeFalse();
        invalidChar.Error.Code.Should().Be("INVALID_LOCATOR_FORMAT");
    }

    [Test]
    public void DiscoveryEpoch_FromUtc_CalculatesUnixEpochDays()
    {
        // 1970-01-02 00:00:00 UTC = 86400 seconds = 1 epoch day
        var time = DateTimeOffset.FromUnixTimeSeconds(86400);
        var epoch = DiscoveryEpoch.FromUtc(time);

        epoch.Value.Should().Be(1);
    }

    [Test]
    public void DiscoveryEpoch_Create_NegativeValueFails()
    {
        var result = DiscoveryEpoch.Create(-1);
        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("INVALID_EPOCH");
    }

    [Test]
    public async Task PrivateRendezvousService_PublishEncryptedAndResolve_PreservesZeroKnowledgeRoutePrivacy()
    {
        var service = new PrivateRendezvousService();
        var publisherId = PublicIdentityId.New();
        var epoch = new DiscoveryEpoch(20000);
        var route = new RelayRouteDescriptor("relay://primary.percolator.net:443");

        var locator = service.DeriveLocator(publisherId, SharedSecret, epoch);
        var encryptedRoute = EncryptedRendezvousRoute.Encrypt(route, locator, SharedSecret, epoch, publisherId);

        var publishResult = await service.PublishEncryptedRendezvousAsync(locator, encryptedRoute);
        publishResult.IsSuccess.Should().BeTrue();

        // Query using 3-epoch clock-skew window
        var queryWindow = service.DeriveLocatorWindow(publisherId, SharedSecret, epoch);
        var matches = await service.ResolveRendezvousAsync(queryWindow);
        matches.Should().ContainSingle();
        matches[0].Locator.Should().Be(locator);

        // Mutual contact with sharedSecret can decrypt the route
        var decrypted = matches[0].EncryptedRoute.Decrypt(locator, SharedSecret, epoch, publisherId);
        decrypted.IsSuccess.Should().BeTrue();
        decrypted.Value.Value.Should().Be("relay://primary.percolator.net:443");

        // Third party or wrong locator AAD cannot decrypt
        var wrongSecret = (byte[])SharedSecret.Clone();
        wrongSecret[0] ^= 0xFF;
        var failedDecrypt = matches[0].EncryptedRoute.Decrypt(locator, wrongSecret, epoch, publisherId);
        failedDecrypt.IsSuccess.Should().BeFalse();
        failedDecrypt.Error.Code.Should().Be("RENDEZVOUS_DECRYPTION_FAILED");
    }

    [Test]
    public async Task PrivateRendezvousService_PublishAndResolve_MatchesLocator()
    {
        var service = new PrivateRendezvousService();
        var peerId = PublicIdentityId.New();
        var epoch = new DiscoveryEpoch(20000);

        var locator = service.DeriveLocator(peerId, SharedSecret, epoch);
        var publishResult = await service.PublishRendezvousAsync(locator, "relay://primary.percolator.net:443");
        publishResult.IsSuccess.Should().BeTrue();

        var matches = await service.ResolveRendezvousAsync(new[] { locator });
        matches.Should().ContainSingle();
        matches[0].Locator.Should().Be(locator);
        matches[0].RelayRouteDescriptor.Should().Be(new RelayRouteDescriptor("relay://primary.percolator.net:443"));
    }
}
