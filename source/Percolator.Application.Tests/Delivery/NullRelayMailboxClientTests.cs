using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Application.Delivery;
using Percolator.Application.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;

namespace Percolator.Application.Tests.Delivery;

[TestFixture]
public sealed class NullRelayMailboxClientTests
{
    [Test]
    public async Task OpenMailboxStreamAsync_ReturnsClientUnavailableError()
    {
        var client = new NullRelayMailboxClient();
        var endpoint = RelayEndpoint.FromHostAndPort("127.0.0.1", 5200);
        var tokens = new MailboxTokenTriad(
            BlindedRoutingToken.New(),
            DeliveryToken.GenerateRandom(),
            DeliveryToken.GenerateRandom());

        var result = await client.OpenMailboxStreamAsync(endpoint, tokens);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("RELAY_MAILBOX_CLIENT_UNAVAILABLE");
    }
}
