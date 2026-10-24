using FluentAssertions;
using NUnit.Framework;
using Percolator.Application2.Profiles;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2.Tests.Profiles;

[TestFixture]
public sealed class InvitationLinkTests
{
    [Test]
    public void ToString_WithPort_IncludesPort()
    {
        var link = new InvitationLink("peer.percolator.net", 5222, "PubKey12345");
        link.ToString().Should().Be("percolator://peer.percolator.net:5222/PubKey12345");
    }

    [Test]
    public void ToString_WithoutPort_OmitsPort()
    {
        var link = new InvitationLink("peer.percolator.net", "PubKey12345");
        link.ToString().Should().Be("percolator://peer.percolator.net/PubKey12345");
    }

    [Test]
    public void Parse_WithPort_ParsesCorrectly()
    {
        var raw = "percolator://192.168.1.100:8080/MyIdentityKeyHex";
        var parsed = InvitationLink.Parse(raw);

        parsed.Host.Should().Be("192.168.1.100");
        parsed.Port.Should().Be(8080);
        parsed.PublicKey.Should().Be("MyIdentityKeyHex");
        parsed.Kind.Should().Be(InvitationKind.Direct);
    }

    [Test]
    public void Parse_WithoutPort_ParsesCorrectlyWithNullPort()
    {
        var raw = "percolator://peer.percolator.net/MyIdentityKeyHex";
        var parsed = InvitationLink.Parse(raw);

        parsed.Host.Should().Be("peer.percolator.net");
        parsed.Port.Should().BeNull();
        parsed.PublicKey.Should().Be("MyIdentityKeyHex");
        parsed.Kind.Should().Be(InvitationKind.Direct);
    }

    [Test]
    public void CreateRelayed_Base64Roundtrip_ParsesCorrectly()
    {
        var peerId = PublicIdentityId.New();
        var deviceId = DeviceId.Primary;
        var identityKey = IdentityKey.FromSpan(new byte[32]);
        var routingToken = BlindedRoutingToken.New();
        var deliveryToken = DeliveryToken.FromSpan(new byte[32]);
        const string relayEndpoint = "relay.percolator.net:15003";

        var relayed = InvitationLink.CreateRelayed(
            peerId,
            deviceId,
            identityKey,
            relayEndpoint,
            routingToken,
            deliveryToken);

        relayed.Kind.Should().Be(InvitationKind.Relayed);
        var base64 = relayed.ToBase64();
        base64.Should().NotBeNullOrWhiteSpace();

        // 1. Parse raw Base64
        var parsedFromBase64 = InvitationLink.Parse(base64);
        parsedFromBase64.Kind.Should().Be(InvitationKind.Relayed);
        parsedFromBase64.PeerIdentityId.Should().Be(peerId);
        parsedFromBase64.PeerDeviceId.Should().Be(deviceId);
        parsedFromBase64.IdentityKey.Should().Be(identityKey);
        parsedFromBase64.RelayEndpoint.Should().Be(relayEndpoint);
        parsedFromBase64.RoutingToken.Should().Be(routingToken);
        parsedFromBase64.DeliveryToken.Should().Be(deliveryToken);

        // 2. Parse URI format: percolator://invite/{Base64}
        var uriString = relayed.ToString();
        uriString.Should().StartWith("percolator://invite/");

        var parsedFromUri = InvitationLink.Parse(uriString);
        parsedFromUri.Kind.Should().Be(InvitationKind.Relayed);
        parsedFromUri.PeerIdentityId.Should().Be(peerId);
        parsedFromUri.PeerDeviceId.Should().Be(deviceId);
        parsedFromUri.IdentityKey.Should().Be(identityKey);
        parsedFromUri.RelayEndpoint.Should().Be(relayEndpoint);
        parsedFromUri.RoutingToken.Should().Be(routingToken);
        parsedFromUri.DeliveryToken.Should().Be(deliveryToken);
    }

    [TestCase("http://peer.percolator.net/Key", "Invalid scheme")]
    [TestCase("percolator:///Key", "Host is missing")]
    [TestCase("percolator://peer.percolator.net/", "Public key is missing")]
    [TestCase("percolator://peer.percolator.net", "Public key is missing")]
    [TestCase("", "Invitation link is empty")]
    public void TryParse_InvalidUri_ReturnsFalse(string raw, string expectedErrorSubstring)
    {
        var success = InvitationLink.TryParse(raw, out var result, out var errorMessage);

        success.Should().BeFalse();
        result.Should().BeNull();
        errorMessage.Should().Contain(expectedErrorSubstring);
    }

    [Test]
    public void Parse_InvalidUri_ThrowsFormatException()
    {
        var action = () => InvitationLink.Parse("not-a-valid-uri");
        action.Should().Throw<FormatException>();
    }

    [Test]
    public void Constructor_InvalidPort_ThrowsArgumentOutOfRangeException()
    {
        var action = () => new InvitationLink("host", 0, "pubkey");
        action.Should().Throw<ArgumentOutOfRangeException>();

        var action2 = () => new InvitationLink("host", 70000, "pubkey");
        action2.Should().Throw<ArgumentOutOfRangeException>();
    }
}
