using System;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Percolator.Application.Invitations;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Tests.Topology;

[TestFixture]
public sealed class PersonaInvitationExportFlyoutViewModelTests
{
    private Mock<ISimulatedPersonaHost> _hostMock = null!;
    private Mock<IStatusToastService> _toastServiceMock = null!;
    private IPeerInvitationCodec _codec = null!;
    private PublicIdentityId _testIdentityId;
    private BlindedRoutingToken _routingToken;
    private DeliveryToken _deliveryToken;

    [SetUp]
    public void SetUp()
    {
        _testIdentityId = new PublicIdentityId(Guid.NewGuid());
        _routingToken = new BlindedRoutingToken(Guid.NewGuid());
        _deliveryToken = DeliveryToken.GenerateRandom();

        _hostMock = new Mock<ISimulatedPersonaHost>(MockBehavior.Strict);
        _hostMock.SetupGet(h => h.PublicIdentityId).Returns(_testIdentityId);
        _hostMock.SetupGet(h => h.MailboxRoutingToken).Returns(_routingToken);
        _hostMock.SetupGet(h => h.MailboxDeliveryToken).Returns(_deliveryToken);
        _hostMock.SetupGet(h => h.BoundPort).Returns(5155);

        _toastServiceMock = new Mock<IStatusToastService>(MockBehavior.Loose);
        _codec = new PeerInvitationCodec();
    }

    [TearDown]
    public void TearDown()
    {
        _deliveryToken.Dispose();
    }

    [Test]
    public void UpdateGeneratedInvitation_WithAutoLan_EncodesLanEndpointInUrl()
    {
        const string mockLanIp = "192.168.1.123";
        var vm = new PersonaInvitationExportFlyoutViewModel(
            _hostMock.Object,
            _codec,
            _toastServiceMock.Object,
            lanIpOverride: mockLanIp);

        vm.SelectedHostKind = SimulatorAdvertisedHostKind.AutoLanIp;
        vm.UpdateGeneratedInvitation();

        vm.GeneratedInvitationUrl.Should().StartWith("percolator://invite?");
        var decoded = _codec.Decode(vm.GeneratedInvitationUrl);
        decoded.IsSuccess.Should().BeTrue();
        decoded.Value!.EndpointHosts.Should().Contain(mockLanIp);
    }

    [Test]
    public void UpdateGeneratedInvitation_WithLoopback_EncodesLoopbackInUrl()
    {
        var vm = new PersonaInvitationExportFlyoutViewModel(
            _hostMock.Object,
            _codec,
            _toastServiceMock.Object,
            lanIpOverride: "192.168.1.123");

        vm.SelectedHostKind = SimulatorAdvertisedHostKind.Loopback;
        vm.UpdateGeneratedInvitation();

        var decoded = _codec.Decode(vm.GeneratedInvitationUrl);
        decoded.IsSuccess.Should().BeTrue();
        decoded.Value!.EndpointHosts.Should().Contain("127.0.0.1");
    }

    [Test]
    public void UpdateGeneratedInvitation_WithCustomHost_EncodesCustomHostInUrl()
    {
        var vm = new PersonaInvitationExportFlyoutViewModel(
            _hostMock.Object,
            _codec,
            _toastServiceMock.Object,
            lanIpOverride: "192.168.1.123");

        vm.SelectedHostKind = SimulatorAdvertisedHostKind.Custom;
        vm.CustomHostInput = "node.custom.net";
        vm.UpdateGeneratedInvitation();

        var decoded = _codec.Decode(vm.GeneratedInvitationUrl);
        decoded.IsSuccess.Should().BeTrue();
        decoded.Value!.EndpointHosts.Should().Contain("node.custom.net");
    }

    [Test]
    public void UpdateGeneratedInvitation_WithWhitespaceCustomHost_FallsBackToLoopback()
    {
        var vm = new PersonaInvitationExportFlyoutViewModel(
            _hostMock.Object,
            _codec,
            _toastServiceMock.Object,
            lanIpOverride: "192.168.1.123");

        vm.SelectedHostKind = SimulatorAdvertisedHostKind.Custom;
        vm.CustomHostInput = "   ";
        vm.UpdateGeneratedInvitation();

        var decoded = _codec.Decode(vm.GeneratedInvitationUrl);
        decoded.IsSuccess.Should().BeTrue();
        decoded.Value!.EndpointHosts.Should().Contain("127.0.0.1");
    }

    [Test]
    public void UpdateGeneratedInvitation_WithPortPresetDefault_OmitsPortRange()
    {
        var vm = new PersonaInvitationExportFlyoutViewModel(
            _hostMock.Object,
            _codec,
            _toastServiceMock.Object,
            lanIpOverride: "192.168.1.123");

        vm.SelectedPortPreset = PortRangePrivacyPreset.DefaultRange;
        vm.UpdateGeneratedInvitation();

        vm.GeneratedInvitationUrl.Should().NotContain("&ports=");
        var decoded = _codec.Decode(vm.GeneratedInvitationUrl);
        decoded.IsSuccess.Should().BeTrue();
        decoded.Value!.PortRange.Should().Be(PortRange.DefaultDynamicRange);
    }

    [Test]
    public void UpdateGeneratedInvitation_WithPortPresetCustom_EncodesPortRange()
    {
        var vm = new PersonaInvitationExportFlyoutViewModel(
            _hostMock.Object,
            _codec,
            _toastServiceMock.Object,
            lanIpOverride: "192.168.1.123");

        vm.SelectedPortPreset = PortRangePrivacyPreset.CustomRange;
        vm.CustomPortMin = 6100;
        vm.CustomPortMax = 6200;
        vm.UpdateGeneratedInvitation();

        var decoded = _codec.Decode(vm.GeneratedInvitationUrl);
        decoded.IsSuccess.Should().BeTrue();
        decoded.Value!.PortRange.Should().NotBeNull();
        decoded.Value!.PortRange!.MinPort.Should().Be(6100);
        decoded.Value!.PortRange!.MaxPort.Should().Be(6200);
    }

    [Test]
    public void UpdateGeneratedInvitation_WithInvertedCustomPortRange_NormalizesMinAndMax()
    {
        var vm = new PersonaInvitationExportFlyoutViewModel(
            _hostMock.Object,
            _codec,
            _toastServiceMock.Object,
            lanIpOverride: "192.168.1.123");

        vm.SelectedPortPreset = PortRangePrivacyPreset.CustomRange;
        vm.CustomPortMin = 7000;
        vm.CustomPortMax = 6500; // inverted
        vm.UpdateGeneratedInvitation();

        var decoded = _codec.Decode(vm.GeneratedInvitationUrl);
        decoded.IsSuccess.Should().BeTrue();
        decoded.Value!.PortRange.Should().NotBeNull();
        decoded.Value!.PortRange!.MinPort.Should().Be(6500);
        decoded.Value!.PortRange!.MaxPort.Should().Be(7000);
    }

    [Test]
    public void UpdateGeneratedInvitation_WithPortPresetSinglePort_EncodesSinglePort()
    {
        var vm = new PersonaInvitationExportFlyoutViewModel(
            _hostMock.Object,
            _codec,
            _toastServiceMock.Object,
            lanIpOverride: "192.168.1.123");

        vm.SelectedPortPreset = PortRangePrivacyPreset.SinglePort;
        vm.UpdateGeneratedInvitation();

        var decoded = _codec.Decode(vm.GeneratedInvitationUrl);
        decoded.IsSuccess.Should().BeTrue();
        decoded.Value!.PortRange.Should().NotBeNull();
        decoded.Value!.PortRange!.IsSinglePort.Should().BeTrue();
        decoded.Value!.PortRange!.MinPort.Should().Be(5155);
    }

    [Test]
    public async Task CopyInvitationUrlAsync_ExecutesWithoutError()
    {
        var vm = new PersonaInvitationExportFlyoutViewModel(
            _hostMock.Object,
            _codec,
            _toastServiceMock.Object,
            lanIpOverride: "192.168.1.123");

        var act = () => vm.CopyInvitationUrlAsync();
        await act.Should().NotThrowAsync();
    }
}
