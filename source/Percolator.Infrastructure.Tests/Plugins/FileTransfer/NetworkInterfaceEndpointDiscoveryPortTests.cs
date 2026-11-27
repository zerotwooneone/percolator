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
    public async Task DiscoverLocalCandidatesAsync_WithCancellationToken_ThrowsWhenCanceled()
    {
        var port = new NetworkInterfaceEndpointDiscoveryPort();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await port.DiscoverLocalCandidatesAsync(DirectWanPolicy.DisabledByDefault, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task DiscoverLocalCandidatesAsync_DefaultNullListenPort_ReturnsEmptyBecauseIceGathersEphemeralPorts()
    {
        var port = new NetworkInterfaceEndpointDiscoveryPort();

        var candidates = await port.DiscoverLocalCandidatesAsync(DirectWanPolicy.DisabledByDefault);

        candidates.Should().BeEmpty();
    }

    [Test]
    public async Task DiscoverLocalCandidatesAsync_WithAllowLoopback_IncludesLoopbackCandidate()
    {
        // Arrange
        var port = new NetworkInterfaceEndpointDiscoveryPort(
            listenPort: 49200,
            allowLoopback: true);

        // Act
        var candidates = await port.DiscoverLocalCandidatesAsync(DirectWanPolicy.DisabledByDefault);

        // Assert
        candidates.Should().NotBeEmpty();
        candidates.Should().Contain(c => c.Host.Value == "127.0.0.1" && c.Port.Value == 49200);
    }

    [Test]
    public async Task DiscoverLocalCandidatesAsync_WithCustomPortAndAllowLoopback_UsesCustomPort()
    {
        // Arrange
        var port = new NetworkInterfaceEndpointDiscoveryPort(
            listenPort: 52345,
            allowLoopback: true);

        // Act
        var candidates = await port.DiscoverLocalCandidatesAsync(DirectWanPolicy.DisabledByDefault);

        // Assert
        candidates.Should().Contain(c => c.Host.Value == "127.0.0.1" && c.Port.Value == 52345);
    }

    [Test]
    public async Task AllocateRelayRendezvousAsync_ReturnsNullBecausePipeEndpointIsDiscoveredViaTracker()
    {
        // Arrange
        var port = new NetworkInterfaceEndpointDiscoveryPort(
            listenPort: null,
            allowLoopback: false);

        var sessionId = TransferSessionId.New();

        // Act
        var descriptor = await port.AllocateRelayRendezvousAsync(sessionId);

        // Assert
        descriptor.Should().BeNull();
    }

    [TestCase("127.0.0.1", true)]
    [TestCase("10.0.1.25", true)]
    [TestCase("172.16.0.1", true)]
    [TestCase("172.31.255.254", true)]
    [TestCase("192.168.1.100", true)]
    [TestCase("169.254.1.1", true)]
    [TestCase("100.64.0.1", true)]
    [TestCase("100.127.255.255", true)]
    [TestCase("8.8.8.8", false)]
    [TestCase("142.250.190.46", false)]
    [TestCase("172.32.0.1", false)]
    [TestCase("100.128.0.1", false)]
    [TestCase("::1", true)]
    [TestCase("fe80::1", true)]
    [TestCase("fc00::1", true)]
    [TestCase("fd12:3456:789a::1", true)]
    [TestCase("2001:4860:4860::8888", false)]
    public void IsPrivateOrLinkLocal_ClassifiesCorrectly(string ipString, bool expectedPrivate)
    {
        var ip = System.Net.IPAddress.Parse(ipString);
        NetworkInterfaceEndpointDiscoveryPort.IsPrivateOrLinkLocal(ip).Should().Be(expectedPrivate);
    }
}
