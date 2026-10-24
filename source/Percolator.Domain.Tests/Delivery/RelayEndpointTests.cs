using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Delivery.ValueObjects;

namespace Percolator.Domain.Tests.Delivery;

[TestFixture]
public class RelayEndpointTests
{
    [TestCase("localhost:15003", "localhost", 15003)]
    [TestCase("127.0.0.1:8080", "127.0.0.1", 8080)]
    [TestCase("relay.example.com:443", "relay.example.com", 443)]
    [TestCase("http://relay.example.com:8080", "relay.example.com", 8080)]
    [TestCase("https://relay.example.com:443", "relay.example.com", 443)]
    [TestCase("[::1]:9000", "::1", 9000)]
    public void Create_ValidHostAndPort_ReturnsSuccessWithExpectedProperties(string input, string expectedHost, int expectedPort)
    {
        var result = RelayEndpoint.Create(input);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Host.Should().Be(expectedHost);
        result.Value.Port.Should().Be(expectedPort);
        result.Value.Value.Should().Be(input);
        result.Value.ToString().Should().Be(input);
    }

    [TestCase("localhost", "localhost")]
    [TestCase("relay.example.com", "relay.example.com")]
    [TestCase("[::1]", "::1")]
    public void Create_HostWithoutPort_ReturnsSuccessWithNullPort(string input, string expectedHost)
    {
        var result = RelayEndpoint.Create(input);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Host.Should().Be(expectedHost);
        result.Value.Port.Should().BeNull();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void Create_NullOrWhitespace_ReturnsFailure(string? input)
    {
        var result = RelayEndpoint.Create(input);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_RELAY_ENDPOINT");
    }

    [TestCase("localhost:0")]
    [TestCase("localhost:65536")]
    [TestCase("localhost:-1")]
    [TestCase("localhost:abc")]
    [TestCase("[::1]:99999")]
    public void Create_InvalidPort_ReturnsFailure(string input)
    {
        var result = RelayEndpoint.Create(input);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_RELAY_PORT");
    }

    [Test]
    public void FromHostAndPort_ValidArguments_ConstructsRelayEndpoint()
    {
        var endpoint = RelayEndpoint.FromHostAndPort("relay.percolator.net", 443);

        endpoint.Host.Should().Be("relay.percolator.net");
        endpoint.Port.Should().Be(443);
        endpoint.Value.Should().Be("relay.percolator.net:443");
    }

    [Test]
    public void FromUri_ValidUri_ConstructsRelayEndpoint()
    {
        var uri = new Uri("https://relay.percolator.net:8443");
        var endpoint = RelayEndpoint.FromUri(uri);

        endpoint.Host.Should().Be("relay.percolator.net");
        endpoint.Port.Should().Be(8443);
    }

    [Test]
    public void ValueEquality_TwoInstancesWithSameEndpoint_AreEqual()
    {
        var ep1 = RelayEndpoint.Parse("localhost:15003");
        var ep2 = RelayEndpoint.FromHostAndPort("localhost", 15003);

        ep1.Should().Be(ep2);
        (ep1 == ep2).Should().BeTrue();
        ep1.GetHashCode().Should().Be(ep2.GetHashCode());
    }
}
