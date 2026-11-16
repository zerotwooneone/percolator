// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.
// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/R3.spec.md before modifying this file.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Percolator.Application.Invitations;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Features.Transports.ViewModels;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Desktop.Tests.Features.Transports;

[TestFixture]
public sealed class ShareInvitationDialogViewModelTests
{
    private FakePeerInvitationCodec _fakeCodec = null!;
    private FakeClipboardService _fakeClipboard = null!;
    private FakeQrCodeGenerator _fakeQrGenerator = null!;
    private PublicIdentityId _testIdentityId;
    private BlindedRoutingToken _testRoutingToken;
    private DeliveryToken _testDeliveryToken;
    private readonly string[] _testHosts = new[] { "192.168.1.100", "127.0.0.1" };
    private const int TestBoundPort = 5124;

    [SetUp]
    public void SetUp()
    {
        _fakeCodec = new FakePeerInvitationCodec();
        _fakeClipboard = new FakeClipboardService();
        _fakeQrGenerator = new FakeQrCodeGenerator();
        _testIdentityId = PublicIdentityId.New();
        _testRoutingToken = BlindedRoutingToken.New();
        _testDeliveryToken = DeliveryToken.GenerateRandom();
    }

    [TearDown]
    public void TearDown()
    {
        _testDeliveryToken?.Dispose();
    }

    private ShareInvitationDialogViewModel CreateViewModel()
    {
        return new ShareInvitationDialogViewModel(
            _fakeCodec,
            _fakeClipboard,
            _fakeQrGenerator,
            NullLogger<ShareInvitationDialogViewModel>.Instance);
    }

    [Test]
    public void Constructor_WithNullDependencies_ThrowsArgumentNullException()
    {
        var logger = NullLogger<ShareInvitationDialogViewModel>.Instance;

        Action act1 = () => new ShareInvitationDialogViewModel(null!, _fakeClipboard, _fakeQrGenerator, logger);
        Action act2 = () => new ShareInvitationDialogViewModel(_fakeCodec, null!, _fakeQrGenerator, logger);
        Action act3 = () => new ShareInvitationDialogViewModel(_fakeCodec, _fakeClipboard, null!, logger);
        Action act4 = () => new ShareInvitationDialogViewModel(_fakeCodec, _fakeClipboard, _fakeQrGenerator, null!);

        act1.Should().Throw<ArgumentNullException>();
        act2.Should().Throw<ArgumentNullException>();
        act3.Should().Throw<ArgumentNullException>();
        act4.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Initialize_WithDefaultRangePreset_GeneratesInvitationWithNullPortRange()
    {
        using var vm = CreateViewModel();

        vm.Initialize(
            _testIdentityId,
            _testRoutingToken,
            _testDeliveryToken,
            _testHosts,
            TestBoundPort);

        vm.SelectedPrivacyPreset.Should().Be(PortRangePrivacyPreset.DefaultRange);
        vm.IsDefaultRangePreset.Should().BeTrue();
        vm.IsCustomRangeVisible.Should().BeFalse();
        vm.BoundPort.Should().Be(TestBoundPort);

        _fakeCodec.LastEncodedPayload.Should().NotBeNull();
        _fakeCodec.LastEncodedPayload!.IdentityId.Should().Be(_testIdentityId);
        _fakeCodec.LastEncodedPayload.PortRange.Should().BeNull("default range preset must omit port from invitation");

        vm.InvitationUrl.Should().Be("percolator://invite?test=1");
        vm.QrCodePngBytes.Should().NotBeNull();
        vm.QrCodePngBytes.Should().BeEquivalentTo(_fakeQrGenerator.DefaultBytes);
    }

    [Test]
    public void SelectedPrivacyPreset_WhenCustomRange_EmbedsDiscretePortRange()
    {
        using var vm = CreateViewModel();
        vm.Initialize(_testIdentityId, _testRoutingToken, _testDeliveryToken, _testHosts, TestBoundPort);

        vm.SelectedPrivacyPreset = PortRangePrivacyPreset.CustomRange;
        vm.CustomRangeMin = 5150;
        vm.CustomRangeMax = 5200;

        vm.IsCustomRangeVisible.Should().BeTrue();
        vm.IsCustomRangePreset.Should().BeTrue();

        _fakeCodec.LastEncodedPayload.Should().NotBeNull();
        _fakeCodec.LastEncodedPayload!.PortRange.Should().NotBeNull();
        _fakeCodec.LastEncodedPayload.PortRange!.MinPort.Should().Be(5150);
        _fakeCodec.LastEncodedPayload.PortRange!.MaxPort.Should().Be(5200);
    }

    [Test]
    public void SelectedPrivacyPreset_WhenSinglePort_EmbedsBoundListeningPort()
    {
        using var vm = CreateViewModel();
        vm.Initialize(_testIdentityId, _testRoutingToken, _testDeliveryToken, _testHosts, TestBoundPort);

        vm.SelectedPrivacyPreset = PortRangePrivacyPreset.SinglePort;

        vm.IsSinglePortPreset.Should().BeTrue();
        vm.IsCustomRangeVisible.Should().BeFalse();

        _fakeCodec.LastEncodedPayload.Should().NotBeNull();
        _fakeCodec.LastEncodedPayload!.PortRange.Should().NotBeNull();
        _fakeCodec.LastEncodedPayload.PortRange!.MinPort.Should().Be(TestBoundPort);
        _fakeCodec.LastEncodedPayload.PortRange!.MaxPort.Should().Be(TestBoundPort);
    }

    [Test]
    public async Task CopyInvitationUrlAsync_CopiesUrlToClipboard_AndSetsStatusMessage()
    {
        using var vm = CreateViewModel();
        vm.Initialize(_testIdentityId, _testRoutingToken, _testDeliveryToken, _testHosts, TestBoundPort);

        await vm.CopyInvitationUrlAsync();

        _fakeClipboard.Text.Should().Be(vm.InvitationUrl);
        vm.StatusMessage.Should().Contain("copied to clipboard");
        vm.ErrorMessage.Should().BeNull();
    }

    [Test]
    public void TwoWayPresetProperties_ToggleSelectedPrivacyPresetCorrectly()
    {
        using var vm = CreateViewModel();
        vm.Initialize(_testIdentityId, _testRoutingToken, _testDeliveryToken, _testHosts, TestBoundPort);

        vm.IsCustomRangePreset = true;
        vm.SelectedPrivacyPreset.Should().Be(PortRangePrivacyPreset.CustomRange);
        vm.IsDefaultRangePreset.Should().BeFalse();
        vm.IsSinglePortPreset.Should().BeFalse();
        vm.IsCustomRangeVisible.Should().BeTrue();

        vm.IsSinglePortPreset = true;
        vm.SelectedPrivacyPreset.Should().Be(PortRangePrivacyPreset.SinglePort);
        vm.IsDefaultRangePreset.Should().BeFalse();
        vm.IsCustomRangePreset.Should().BeFalse();
        vm.IsCustomRangeVisible.Should().BeFalse();

        vm.IsDefaultRangePreset = true;
        vm.SelectedPrivacyPreset.Should().Be(PortRangePrivacyPreset.DefaultRange);
        vm.IsDefaultRangePreset.Should().BeTrue();
        vm.IsCustomRangePreset.Should().BeFalse();
        vm.IsSinglePortPreset.Should().BeFalse();

        // Setting false on radio properties does not alter state
        vm.IsCustomRangePreset = false;
        vm.SelectedPrivacyPreset.Should().Be(PortRangePrivacyPreset.DefaultRange);
    }

    [Test]
    public void SelectedPrivacyPreset_WithInvertedCustomRange_NormalizesMinAndMax()
    {
        using var vm = CreateViewModel();
        vm.Initialize(_testIdentityId, _testRoutingToken, _testDeliveryToken, _testHosts, TestBoundPort);

        vm.SelectedPrivacyPreset = PortRangePrivacyPreset.CustomRange;
        // Inverted: min is larger than max
        vm.CustomRangeMin = 5400;
        vm.CustomRangeMax = 5100;

        _fakeCodec.LastEncodedPayload.Should().NotBeNull();
        _fakeCodec.LastEncodedPayload!.PortRange.Should().NotBeNull();
        _fakeCodec.LastEncodedPayload.PortRange!.MinPort.Should().Be(5100);
        _fakeCodec.LastEncodedPayload.PortRange!.MaxPort.Should().Be(5400);
    }

    [Test]
    public void UpdateInvitationPayload_WhenCodecThrows_SetsErrorMessage()
    {
        using var vm = CreateViewModel();
        _fakeCodec.ShouldThrowOnEncode = true;

        vm.Initialize(_testIdentityId, _testRoutingToken, _testDeliveryToken, _testHosts, TestBoundPort);

        vm.ErrorMessage.Should().Be("Failed to generate invitation.");
    }

    [Test]
    public async Task CopyInvitationUrlAsync_WhenUrlIsEmpty_DoesNotInvokeClipboard()
    {
        using var vm = CreateViewModel();
        // Do not initialize, so InvitationUrl remains empty

        await vm.CopyInvitationUrlAsync();

        _fakeClipboard.Text.Should().BeNull();
        vm.StatusMessage.Should().BeNull();
    }

    [Test]
    public void Close_TriggersRequestCloseEvent()
    {
        using var vm = CreateViewModel();
        var closed = false;
        vm.RequestClose += (_, _) => closed = true;

        vm.CloseCommand.Execute(null);

        closed.Should().BeTrue();
    }
}

internal sealed class FakePeerInvitationCodec : IPeerInvitationCodec
{
    public PeerInvitationPayload? LastEncodedPayload { get; private set; }
    public bool ShouldThrowOnEncode { get; set; }

    public string Encode(PeerInvitationPayload payload)
    {
        if (ShouldThrowOnEncode)
        {
            throw new InvalidOperationException("Codec encoding error");
        }

        LastEncodedPayload = payload;
        return "percolator://invite?test=1";
    }

    public DomainResult<PeerInvitationPayload> Decode(string invitationString)
    {
        throw new NotImplementedException();
    }
}

internal sealed class FakeQrCodeGenerator : IQrCodeGenerator
{
    public byte[] DefaultBytes { get; } = new byte[] { 0x89, 0x50, 0x4E, 0x47 };

    public byte[] GeneratePngBytes(string content)
    {
        return DefaultBytes;
    }
}
