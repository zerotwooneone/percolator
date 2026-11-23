namespace Percolator.Application.Tests.Invitations;

using System;
using System.Security.Cryptography;
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
    public void Encode_ProducesUrlWithPercolatorInvitePrefix()
    {
        var id = PublicIdentityId.New();
        var key = IdentityKey.FromSpan(RandomNumberGenerator.GetBytes(32));
        var endpoint = RelayEndpoint.Create("percolator://relay.example.com").Value;
        var route = BlindedRoutingToken.New();

        var payload = new PeerInvitationPayload(id, key, endpoint, route);
        var url = _codec.Encode(payload);

        url.Should().StartWith("percolator://invite/");
    }

    [Test]
    public void Decode_RoundtripUrl_PreservesAllFields()
    {
        var id = PublicIdentityId.New();
        var key = IdentityKey.FromSpan(RandomNumberGenerator.GetBytes(32));
        var endpoint = RelayEndpoint.Create("percolator://relay.example.com:5100").Value;
        var route = BlindedRoutingToken.New();

        var original = new PeerInvitationPayload(id, key, endpoint, route);
        var encoded = _codec.Encode(original);

        var result = _codec.Decode(encoded);
        result.IsSuccess.Should().BeTrue();
        result.Value.IdentityId.Should().Be(id);
        result.Value.IdentityKey.Should().Be(key);
        result.Value.RelayEndpoint.Should().Be(endpoint);
        result.Value.RoutingToken.Should().Be(route);
    }

    [Test]
    public void Decode_WithoutPercolatorPrefix_ReturnsFailure()
    {
        var id = PublicIdentityId.New();
        var key = IdentityKey.FromSpan(RandomNumberGenerator.GetBytes(32));
        var endpoint = RelayEndpoint.Create("percolator://relay.local").Value;
        var route = BlindedRoutingToken.New();

        var payload = new PeerInvitationPayload(id, key, endpoint, route);
        var url = _codec.Encode(payload);
        var stripped = url.Replace("percolator://invite/", "");

        var result = _codec.Decode(stripped);
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_INVITATION_URI");
    }

    [Test]
    public void Decode_EmptyOrWhitespace_ReturnsFailure()
    {
        _codec.Decode("").IsFailure.Should().BeTrue();
        _codec.Decode("   ").IsFailure.Should().BeTrue();
        _codec.Decode(null!).IsFailure.Should().BeTrue();
    }

    [Test]
    public void Decode_GarbageString_ReturnsFailure()
    {
        var result = _codec.Decode("RandomGarbageStringThatIsNotAUrlOrValidBinaryPayload!");
        result.IsFailure.Should().BeTrue();
    }

    [Test]
    public void Decode_UnsupportedVersion_ReturnsFailure()
    {
        var raw = new byte[67];
        raw[0] = 0x02; // version 2 (unsupported)

        var base64Url = System.Buffers.Text.Base64Url.EncodeToString(raw);
        var result = _codec.Decode($"percolator://invite/{base64Url}");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("UNSUPPORTED_VERSION");
    }

    [Test]
    public void Decode_TruncatedBinaryPayload_ReturnsFailure()
    {
        var raw = new byte[30]; // less than 67 bytes minimum
        raw[0] = 0x01;

        var base64Url = System.Buffers.Text.Base64Url.EncodeToString(raw);
        var result = _codec.Decode($"percolator://invite/{base64Url}");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_INVITATION_PAYLOAD");
    }

    [Test]
    public void ToDomain_And_FromDomain_RoundtripsSuccessfully()
    {
        var id = PublicIdentityId.New();
        var key = IdentityKey.FromSpan(RandomNumberGenerator.GetBytes(32));
        var endpoint = RelayEndpoint.Create("percolator://relay.domain.com").Value;
        var route = BlindedRoutingToken.New();

        var original = new PeerInvitationPayload(id, key, endpoint, route);
        var domainInvitation = original.ToDomain().Value;

        domainInvitation.InviterIdentityId.Should().Be(id);
        domainInvitation.InviterIdentityKey.Should().Be(key);
        domainInvitation.RelayEndpoint.Should().Be(endpoint);
        domainInvitation.RoutingToken.Should().Be(route);

        var restoredPayload = PeerInvitationPayload.FromDomain(domainInvitation);
        restoredPayload.Should().Be(original);
    }
}
