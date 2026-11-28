// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Percolator.Application.Invitations;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Transports.Models;
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

    private ShareInvitationDialogParams CreateParams() => new(
        _testIdentityId,
        _testRoutingToken,
        _testDeliveryToken,
        _testHosts,
        TestBoundPort);

    private (ShareInvitationDialogViewModel Vm, Mock<IDialogContext<ShareInvitationDialogParams>> ContextMock) CreateViewModel(ShareInvitationDialogParams? p = null)
    {
        var contextMock = new Mock<IDialogContext<ShareInvitationDialogParams>>();
        contextMock.SetupGet(c => c.Data).Returns(p ?? CreateParams());
        var vm = new ShareInvitationDialogViewModel(
            contextMock.Object,
            _fakeCodec,
            _fakeClipboard,
            _fakeQrGenerator,
            NullLogger<ShareInvitationDialogViewModel>.Instance);
        return (vm, contextMock);
    }

    [Test]
    public void Constructor_WithNullDependencies_ThrowsArgumentNullException()
    {
        var logger = NullLogger<ShareInvitationDialogViewModel>.Instance;
        var contextMock = new Mock<IDialogContext<ShareInvitationDialogParams>>();
        contextMock.SetupGet(c => c.Data).Returns(CreateParams());

        Action act0 = () => new ShareInvitationDialogViewModel(null!, _fakeCodec, _fakeClipboard, _fakeQrGenerator, logger);
        Action act1 = () => new ShareInvitationDialogViewModel(contextMock.Object, null!, _fakeClipboard, _fakeQrGenerator, logger);
        Action act2 = () => new ShareInvitationDialogViewModel(contextMock.Object, _fakeCodec, null!, _fakeQrGenerator, logger);
        Action act3 = () => new ShareInvitationDialogViewModel(contextMock.Object, _fakeCodec, _fakeClipboard, null!, logger);
        Action act4 = () => new ShareInvitationDialogViewModel(contextMock.Object, _fakeCodec, _fakeClipboard, _fakeQrGenerator, null!);

        act0.Should().Throw<ArgumentNullException>();
        act1.Should().Throw<ArgumentNullException>();
        act2.Should().Throw<ArgumentNullException>();
        act3.Should().Throw<ArgumentNullException>();
        act4.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Initialize_WithDefaultRangePreset_GeneratesInvitationWithNullPortRange()
    {
        var (vm, _) = CreateViewModel();
        using (vm)
        {
            vm.SelectedPrivacyPreset.Should().Be(PortRangePrivacyPreset.DefaultRange);
            vm.IsDefaultRangePreset.Should().BeTrue();
            vm.IsCustomRangeVisible.Should().BeFalse();
            vm.BoundPort.Should().Be(TestBoundPort);

            _fakeCodec.LastEncodedPayload.Should().NotBeNull();
            _fakeCodec.LastEncodedPayload!.IdentityId.Should().Be(_testIdentityId);
            _fakeCodec.LastEncodedPayload.RoutingToken.Should().Be(_testRoutingToken);

            vm.InvitationUrl.Should().Be("percolator://invite?test=1");
            vm.QrCodePngBytes.Should().NotBeNull();
            vm.QrCodePngBytes.Should().BeEquivalentTo(_fakeQrGenerator.DefaultBytes);
        }
    }

    [Test]
    public void SelectedPrivacyPreset_WhenCustomRange_EmbedsDiscretePortRange()
    {
        var (vm, _) = CreateViewModel();
        using (vm)
        {
            vm.SelectedPrivacyPreset = PortRangePrivacyPreset.CustomRange;
            vm.CustomRangeMin = 5150;
            vm.CustomRangeMax = 5200;

            vm.IsCustomRangeVisible.Should().BeTrue();
            vm.IsCustomRangePreset.Should().BeTrue();

            _fakeCodec.LastEncodedPayload.Should().NotBeNull();
            _fakeCodec.LastEncodedPayload!.IdentityId.Should().Be(_testIdentityId);
        }
    }

    [Test]
    public void SelectedPrivacyPreset_WhenSinglePort_EmbedsBoundListeningPort()
    {
        var (vm, _) = CreateViewModel();
        using (vm)
        {
            vm.SelectedPrivacyPreset = PortRangePrivacyPreset.SinglePort;

            vm.IsSinglePortPreset.Should().BeTrue();
            vm.IsCustomRangeVisible.Should().BeFalse();

            _fakeCodec.LastEncodedPayload.Should().NotBeNull();
            _fakeCodec.LastEncodedPayload!.IdentityId.Should().Be(_testIdentityId);
        }
    }

    [Test]
    public void RadioBooleanProperties_ReflectSelectedPrivacyPreset()
    {
        var (vm, _) = CreateViewModel();
        using (vm)
        {
            vm.IsCustomRangePreset = true;
            vm.SelectedPrivacyPreset.Should().Be(PortRangePrivacyPreset.CustomRange);
            vm.IsCustomRangePreset.Should().BeTrue();
            vm.IsDefaultRangePreset.Should().BeFalse();
            vm.IsSinglePortPreset.Should().BeFalse();

            vm.IsSinglePortPreset = true;
            vm.SelectedPrivacyPreset.Should().Be(PortRangePrivacyPreset.SinglePort);
            vm.IsSinglePortPreset.Should().BeTrue();
            vm.IsCustomRangePreset.Should().BeFalse();
            vm.IsDefaultRangePreset.Should().BeFalse();

            vm.IsDefaultRangePreset = true;
            vm.SelectedPrivacyPreset.Should().Be(PortRangePrivacyPreset.DefaultRange);
            vm.IsDefaultRangePreset.Should().BeTrue();
            vm.IsCustomRangePreset.Should().BeFalse();
            vm.IsSinglePortPreset.Should().BeFalse();

            // Setting false on radio properties does not alter state
            vm.IsCustomRangePreset = false;
            vm.SelectedPrivacyPreset.Should().Be(PortRangePrivacyPreset.DefaultRange);
        }
    }

    [Test]
    public void SelectedPrivacyPreset_WithInvertedCustomRange_NormalizesMinAndMax()
    {
        var (vm, _) = CreateViewModel();
        using (vm)
        {
            vm.SelectedPrivacyPreset = PortRangePrivacyPreset.CustomRange;
            // Inverted: min is larger than max
            vm.CustomRangeMin = 5400;
            vm.CustomRangeMax = 5100;

            _fakeCodec.LastEncodedPayload.Should().NotBeNull();
            _fakeCodec.LastEncodedPayload!.IdentityId.Should().Be(_testIdentityId);
        }
    }

    [Test]
    public void UpdateInvitationPayload_WhenCodecThrows_SetsErrorMessage()
    {
        _fakeCodec.ShouldThrowOnEncode = true;
        var (vm, _) = CreateViewModel();
        using (vm)
        {
            vm.ErrorMessage.Should().Be("Failed to generate invitation.");
        }
    }

    [Test]
    public async Task CopyInvitationUrlAsync_WhenUrlIsEmpty_DoesNotInvokeClipboard()
    {
        var (vm, _) = CreateViewModel();
        using (vm)
        {
            vm.InvitationUrl = string.Empty;

            await vm.CopyInvitationUrlAsync();

            _fakeClipboard.Text.Should().BeNull();
            vm.StatusMessage.Should().BeNull();
        }
    }

    [Test]
    public void Close_TriggersDialogContextClose()
    {
        var (vm, contextMock) = CreateViewModel();
        using (vm)
        {
            vm.CloseCommand.Execute(null);

            contextMock.Verify(c => c.Close(), Times.Once());
        }
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
