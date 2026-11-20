namespace Percolator.Infrastructure.Tests.Plugins.FileTransfer;

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Infrastructure.Plugins.FileTransfer;

[TestFixture]
public sealed class NetworkInterfaceEndpointDiscoveryPortTests
{
    [Test]
    public void Constructor_WithInvalidPort_ThrowsArgumentOutOfRangeException()
    {
        var actZero = () => new NetworkInterfaceEndpointDiscoveryPort(listenPort: 0);
        actZero.Should().Throw<ArgumentOutOfRangeException>();

        var actTooHigh = () => new NetworkInterfaceEndpointDiscoveryPort(listenPort: 70000);
        actTooHigh.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void Constructor_WithInvalidUri_ThrowsArgumentException()
    {
        var actNull = () => new NetworkInterfaceEndpointDiscoveryPort(relayBaseUri: "");
        actNull.Should().Throw<ArgumentException>();

        var actRelative = () => new NetworkInterfaceEndpointDiscoveryPort(relayBaseUri: "not-a-valid-uri");
        actRelative.Should().Throw<ArgumentException>();
    }

    [Test]
    public async Task DiscoverLocalCandidatesAsync_WithCancellationToken_ThrowsWhenCanceled()
    {
        var port = new NetworkInterfaceEndpointDiscoveryPort();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await port.DiscoverLocalCandidatesAsync(DirectWanPolicy.DisabledByDefault, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task DiscoverLocalCandidatesAsync_WithAllowLoopback_IncludesLoopbackCandidate()
    {
        // Arrange
        var port = new NetworkInterfaceEndpointDiscoveryPort(
            listenPort: 50001,
            relayBaseUri: "https://relay.percolator.net/pipes",
            allowLoopback: true);

        // Act
        var candidates = await port.DiscoverLocalCandidatesAsync(DirectWanPolicy.DisabledByDefault);

        // Assert
        candidates.Should().NotBeEmpty();
        candidates.Should().Contain(c => c.Host.Value == "127.0.0.1" && c.Port.Value == 50001);
    }

    [Test]
    public async Task DiscoverLocalCandidatesAsync_WithCustomPortAndAllowLoopback_UsesCustomPort()
    {
        // Arrange
        var port = new NetworkInterfaceEndpointDiscoveryPort(
            listenPort: 52345,
            relayBaseUri: "https://relay.percolator.net/pipes",
            allowLoopback: true);

        // Act
        var candidates = await port.DiscoverLocalCandidatesAsync(DirectWanPolicy.DisabledByDefault);

        // Assert
        candidates.Should().Contain(c => c.Host.Value == "127.0.0.1" && c.Port.Value == 52345);
    }

    [Test]
    public async Task AllocateRelayRendezvousAsync_ReturnsDescriptorWithConfiguredUri()
    {
        // Arrange
        var customUri = "https://custom.relay.net/pipes";
        var port = new NetworkInterfaceEndpointDiscoveryPort(
            listenPort: 50001,
            relayBaseUri: customUri,
            allowLoopback: false);

        var sessionId = TransferSessionId.New();

        // Act
        var descriptor = await port.AllocateRelayRendezvousAsync(sessionId);

        // Assert
        descriptor.Should().NotBeNull();
        descriptor!.EndpointUri.Value.Should().Be(customUri);
        descriptor.Token.Memory.Length.Should().Be(32);
    }
}
