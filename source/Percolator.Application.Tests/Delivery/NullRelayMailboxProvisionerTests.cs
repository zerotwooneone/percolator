using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Application.Delivery;
using Percolator.Application.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application.Tests.Delivery;

[TestFixture]
public sealed class NullRelayMailboxProvisionerTests
{
    private readonly NullRelayMailboxProvisioner _provisioner = new();
    private readonly PublicIdentityId _owner = PublicIdentityId.New();
    private readonly PublicIdentityId _relay = PublicIdentityId.New();
    private readonly RelayEndpoint _endpoint = RelayEndpoint.FromHostAndPort("127.0.0.1", 5200);
    private readonly MailboxTokenTriad _tokens = new(
        BlindedRoutingToken.New(),
        DeliveryToken.GenerateRandom(),
        DeliveryToken.GenerateRandom());

    [Test]
    public async Task ProvisionMailboxAsync_ReturnsProvisionerUnavailableError()
    {
        var result = await _provisioner.ProvisionMailboxAsync(_owner, _relay, _endpoint, _tokens);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("PROVISIONER_UNAVAILABLE");
    }

    [Test]
    public async Task PublishPreKeyBundleAsync_ReturnsProvisionerUnavailableError()
    {
        var result = await _provisioner.PublishPreKeyBundleAsync(_owner, DeviceId.Primary, _endpoint, _tokens, 100u);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("PROVISIONER_UNAVAILABLE");
    }

    [Test]
    public async Task GetPreKeyStatusAsync_ReturnsProvisionerUnavailableError()
    {
        var result = await _provisioner.GetPreKeyStatusAsync(DeviceId.Primary, _endpoint, _tokens);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("PROVISIONER_UNAVAILABLE");
    }
}
