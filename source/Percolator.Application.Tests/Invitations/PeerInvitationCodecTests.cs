namespace Percolator.Application.Tests.Invitations;

using System;
using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Application.Invitations;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;

[TestFixture]
public sealed class PeerInvitationCodecTests
{
    private PeerInvitationCodec _codec = null!;

    [SetUp]
    public void SetUp()
    {
        _codec = new PeerInvitationCodec();
    }

    [Test]
    public void Encode_NullPayload_ThrowsArgumentNullException()
    {
        Action act = () => _codec.Encode(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Encode_ProducesUrlWithPercolatorSchemeAndParameters()
    {
        var id = PublicIdentityId.New();
        var route = BlindedRoutingToken.New();
        var deliv = DeliveryToken.GenerateRandom();
        var hosts = new List<string> { "192.168.1.50", "10.0.0.5" };
        var ports = new PortRange(5100, 5200);

        var payload = new PeerInvitationPayload(id, route, deliv, hosts, ports);
        var url = _codec.Encode(payload);

        url.Should().StartWith("percolator://invite?");
        url.Should().Contain($"id={id.Value:D}");
        url.Should().Contain($"route={route.Value:N}");
        url.Should().Contain($"deliv={Convert.ToHexString(deliv.Span).ToLowerInvariant()}");
        url.Should().Contain("endpoints=192.168.1.50%2C10.0.0.5");
        url.Should().Contain("ports=5100-5200");
    }

    [Test]
    public void Encode_SinglePort_EmbedsSingleNumber()
    {
        var id = PublicIdentityId.New();
        var route = BlindedRoutingToken.New();
        var deliv = DeliveryToken.GenerateRandom();
        var payload = new PeerInvitationPayload(id, route, deliv, new[] { "127.0.0.1" }, PortRange.Single(5123));

        var url = _codec.Encode(payload);
        url.Should().Contain("ports=5123");
    }

    [Test]
    public void Encode_NullPortRange_OmitsPortsQueryParam()
    {
        var id = PublicIdentityId.New();
        var route = BlindedRoutingToken.New();
        var deliv = DeliveryToken.GenerateRandom();
        var payload = new PeerInvitationPayload(id, route, deliv, new[] { "127.0.0.1" }, null);

        var url = _codec.Encode(payload);
        url.Should().NotContain("ports=");
    }

    [Test]
    public void Decode_RoundtripUrl_PreservesAllFields()
    {
        var id = PublicIdentityId.New();
        var route = BlindedRoutingToken.New();
        var deliv = DeliveryToken.GenerateRandom();
        var hosts = new[] { "192.168.1.100", "myhost.local" };
        var ports = new PortRange(5110, 5190);

        var original = new PeerInvitationPayload(id, route, deliv, hosts, ports);
        var encoded = _codec.Encode(original);

        var result = _codec.Decode(encoded);
        result.IsSuccess.Should().BeTrue();
        result.Value.IdentityId.Should().Be(id);
        result.Value.RoutingToken.Should().Be(route);
        result.Value.DeliveryToken.Should().Be(deliv);
        result.Value.EndpointHosts.Should().Equal(hosts);
        result.Value.PortRange.Should().Be(ports);
    }

    [Test]
    public void Decode_RoundtripBase64_PreservesAllFields()
    {
        var id = PublicIdentityId.New();
        var route = BlindedRoutingToken.New();
        var deliv = DeliveryToken.GenerateRandom();
        var hosts = new[] { "10.0.0.1", "172.16.0.1" };
        var ports = new PortRange(5150, 5250);

        var original = new PeerInvitationPayload(id, route, deliv, hosts, ports);
        var base64 = _codec.EncodeAsBase64(original);

        var result = _codec.Decode(base64);
        result.IsSuccess.Should().BeTrue();
        result.Value.IdentityId.Should().Be(id);
        result.Value.RoutingToken.Should().Be(route);
        result.Value.DeliveryToken.Should().Be(deliv);
        result.Value.EndpointHosts.Should().Equal(hosts);
        result.Value.PortRange.Should().Be(ports);
    }

    [Test]
    public void Decode_WithoutPercolatorPrefix_DecodesSuccessfully()
    {
        var id = PublicIdentityId.New();
        var route = BlindedRoutingToken.New();
        var deliv = DeliveryToken.GenerateRandom();
        var payload = new PeerInvitationPayload(id, route, deliv, new[] { "127.0.0.1" }, PortRange.Single(5100));

        var url = _codec.Encode(payload);
        var stripped = url.Replace("percolator://", "");

        var result = _codec.Decode(stripped);
        result.IsSuccess.Should().BeTrue();
        result.Value.IdentityId.Should().Be(id);
    }

    [Test]
    public void Decode_OmittedPorts_DefaultsToDefaultDynamicRange()
    {
        var id = PublicIdentityId.New();
        var route = BlindedRoutingToken.New();
        var deliv = DeliveryToken.GenerateRandom();

        var query = $"invite?id={id.Value:D}&route={route.Value:N}&deliv={Convert.ToHexString(deliv.Span)}&endpoints=127.0.0.1";
        var result = _codec.Decode(query);

        result.IsSuccess.Should().BeTrue();
        result.Value.PortRange.Should().Be(PortRange.DefaultDynamicRange);
    }

    [Test]
    public void Decode_EmptyOrWhitespace_ReturnsFailure()
    {
        _codec.Decode("").IsFailure.Should().BeTrue();
        _codec.Decode("   ").IsFailure.Should().BeTrue();
        _codec.Decode(null!).IsFailure.Should().BeTrue();
    }

    [Test]
    public void Decode_MissingId_ReturnsFailure()
    {
        var route = BlindedRoutingToken.New();
        var deliv = DeliveryToken.GenerateRandom();
        var query = $"invite?route={route.Value:N}&deliv={Convert.ToHexString(deliv.Span)}";

        var result = _codec.Decode(query);
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_IDENTITY_ID");
    }

    [Test]
    public void Decode_InvalidRoutingToken_ReturnsFailure()
    {
        var id = PublicIdentityId.New();
        var deliv = DeliveryToken.GenerateRandom();
        var query = $"invite?id={id.Value:D}&route=not-a-guid&deliv={Convert.ToHexString(deliv.Span)}";

        var result = _codec.Decode(query);
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_ROUTING_TOKEN");
    }

    [Test]
    public void Decode_InvalidDeliveryToken_ReturnsFailure()
    {
        var id = PublicIdentityId.New();
        var route = BlindedRoutingToken.New();
        var query = $"invite?id={id.Value:D}&route={route.Value:N}&deliv=not-hex";

        var result = _codec.Decode(query);
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_DELIVERY_TOKEN");
    }

    [Test]
    public void Decode_DeliveryTokenWrongLength_ReturnsFailure()
    {
        var id = PublicIdentityId.New();
        var route = BlindedRoutingToken.New();
        // 16 bytes hex (32 chars) instead of 32 bytes (64 chars)
        var shortHex = "0102030405060708090a0b0c0d0e0f10";
        var query = $"invite?id={id.Value:D}&route={route.Value:N}&deliv={shortHex}";

        var result = _codec.Decode(query);
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_DELIVERY_TOKEN");
    }

    [Test]
    public void Decode_InvalidPortRange_ReturnsFailure()
    {
        var id = PublicIdentityId.New();
        var route = BlindedRoutingToken.New();
        var deliv = DeliveryToken.GenerateRandom();
        // Min > Max
        var query = $"invite?id={id.Value:D}&route={route.Value:N}&deliv={Convert.ToHexString(deliv.Span)}&ports=6000-5000";

        var result = _codec.Decode(query);
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_PORT_RANGE");
    }

    [Test]
    public void Decode_GarbageString_ReturnsFailure()
    {
        var result = _codec.Decode("RandomGarbageStringThatIsNotAUrlOrValidBinaryPayload!");
        result.IsFailure.Should().BeTrue();
    }

    [Test]
    public void Decode_UrlWithFragment_StripsFragmentAndSucceeds()
    {
        var id = PublicIdentityId.New();
        var route = BlindedRoutingToken.New();
        var deliv = DeliveryToken.GenerateRandom();
        var url = $"percolator://invite?id={id.Value:D}&route={route.Value:N}&deliv={Convert.ToHexStringLower(deliv.Span)}&ports=5100-5200#localFragmentNotes";

        var result = _codec.Decode(url);
        result.IsSuccess.Should().BeTrue();
        result.Value.PortRange.Should().Be(new PortRange(5100, 5200));
    }

    [Test]
    public void Decode_UppercaseDeliveryHex_DecodesSuccessfully()
    {
        var id = PublicIdentityId.New();
        var route = BlindedRoutingToken.New();
        var deliv = DeliveryToken.GenerateRandom();
        var url = $"invite?id={id.Value:D}&route={route.Value:N}&deliv={Convert.ToHexString(deliv.Span).ToUpperInvariant()}";

        var result = _codec.Decode(url);
        result.IsSuccess.Should().BeTrue();
        result.Value.DeliveryToken.Should().Be(deliv);
    }

    [Test]
    public void Decode_BinaryPayloadTooLarge_ReturnsFailure()
    {
        var oversized = new byte[4097];
        var base64 = Convert.ToBase64String(oversized);

        var result = _codec.Decode(base64);
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("PAYLOAD_TOO_LARGE");
    }

    [Test]
    public void Decode_BinaryPayloadExcessiveHosts_ReturnsFailure()
    {
        var id = PublicIdentityId.New();
        var route = BlindedRoutingToken.New();
        var deliv = DeliveryToken.GenerateRandom();

        using var ms = new System.IO.MemoryStream();
        using var writer = new System.IO.BinaryWriter(ms, System.Text.Encoding.UTF8);
        writer.Write((byte)1); // version
        writer.Write(id.ToByteArray());
        writer.Write(route.ToByteArray());
        writer.Write(deliv.Span);
        writer.Write((byte)0); // no port range
        writer.Write((ushort)65); // excessive host count > 64

        var base64 = Convert.ToBase64String(ms.ToArray());
        var result = _codec.Decode(base64);
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_INVITATION_PAYLOAD");
    }
}
