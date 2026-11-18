using System;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Percolator.Simulator.Features.RelayedChat;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Tests.RelayedChat;

[TestFixture]
public sealed class RelayEndpointExportFlyoutViewModelTests
{
    private Mock<ISimulatedRelayHost> _relayHostMock = null!;
    private Mock<IStatusToastService> _toastServiceMock = null!;

    [SetUp]
    public void SetUp()
    {
        _relayHostMock = new Mock<ISimulatedRelayHost>(MockBehavior.Strict);
        _relayHostMock.SetupGet(r => r.BoundPort).Returns(8443);
        _toastServiceMock = new Mock<IStatusToastService>(MockBehavior.Loose);
    }

    [Test]
    public void UpdateGeneratedRelayUrl_WithAutoLan_GeneratesCorrectHttpsUrl()
    {
        const string mockLanIp = "192.168.1.150";
        var vm = new RelayEndpointExportFlyoutViewModel(
            _relayHostMock.Object,
            _toastServiceMock.Object,
            lanIpOverride: mockLanIp);

        vm.SelectedHostKind = SimulatorAdvertisedHostKind.AutoLanIp;
        vm.UpdateGeneratedRelayUrl();

        vm.GeneratedRelayUrl.Should().Be("https://192.168.1.150:8443");
    }

    [Test]
    public void UpdateGeneratedRelayUrl_WithLoopback_GeneratesLoopbackUrl()
    {
        var vm = new RelayEndpointExportFlyoutViewModel(
            _relayHostMock.Object,
            _toastServiceMock.Object,
            lanIpOverride: "192.168.1.150");

        vm.SelectedHostKind = SimulatorAdvertisedHostKind.Loopback;
        vm.UpdateGeneratedRelayUrl();

        vm.GeneratedRelayUrl.Should().Be("https://127.0.0.1:8443");
    }

    [Test]
    public void UpdateGeneratedRelayUrl_WithCustomHost_GeneratesCustomUrl()
    {
        var vm = new RelayEndpointExportFlyoutViewModel(
            _relayHostMock.Object,
            _toastServiceMock.Object,
            lanIpOverride: "192.168.1.150");

        vm.SelectedHostKind = SimulatorAdvertisedHostKind.Custom;
        vm.CustomHostInput = "relay.myhost.org";
        vm.UpdateGeneratedRelayUrl();

        vm.GeneratedRelayUrl.Should().Be("https://relay.myhost.org:8443");
    }

    [Test]
    public void UpdateGeneratedRelayUrl_WithIPv6CustomHost_FormatsBracketsCorrectly()
    {
        var vm = new RelayEndpointExportFlyoutViewModel(
            _relayHostMock.Object,
            _toastServiceMock.Object,
            lanIpOverride: "192.168.1.150");

        vm.SelectedHostKind = SimulatorAdvertisedHostKind.Custom;
        vm.CustomHostInput = "2001:db8::1";
        vm.UpdateGeneratedRelayUrl();

        vm.GeneratedRelayUrl.Should().Be("https://[2001:db8::1]:8443");
    }

    [Test]
    public void UpdateGeneratedRelayUrl_WithWhitespaceCustomHost_FallsBackToLoopback()
    {
        var vm = new RelayEndpointExportFlyoutViewModel(
            _relayHostMock.Object,
            _toastServiceMock.Object,
            lanIpOverride: "192.168.1.150");

        vm.SelectedHostKind = SimulatorAdvertisedHostKind.Custom;
        vm.CustomHostInput = "   ";
        vm.UpdateGeneratedRelayUrl();

        vm.GeneratedRelayUrl.Should().Be("https://127.0.0.1:8443");
    }

    [Test]
    public async Task CopyRelayUrlAsync_ExecutesWithoutError()
    {
        var vm = new RelayEndpointExportFlyoutViewModel(
            _relayHostMock.Object,
            _toastServiceMock.Object,
            lanIpOverride: "192.168.1.150");

        var act = () => vm.CopyRelayUrlAsync();
        await act.Should().NotThrowAsync();
    }
}
