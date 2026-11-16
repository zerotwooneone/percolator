// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Percolator.Application.Delivery.Ports;
using Percolator.Application.Invitations;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Features.Startup.Models;
using Percolator.Desktop.Features.Transports.Models;
using Percolator.Desktop.Features.Transports.ViewModels;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Model;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Desktop.Tests.Features.Transports;

[TestFixture]
public sealed class AddConnectionDialogViewModelTests
{
    private Mock<IPeerInvitationCodec> _mockCodec = null!;
    private Mock<IPeerConnectionService> _mockPeerConnectionService = null!;
    private Mock<IRelayDirectory> _mockRelayDirectory = null!;
    private FakeRelayMailboxProvisioner _fakeRelayProvisioner = null!;
    private FakeClipboardService _fakeClipboardService = null!;
    private Mock<IPersonaDirectory> _mockPersonaDirectory = null!;

    private PublicIdentityId _testPersonaId;
    private PeerInvitationPayload _testInvitationPayload = null!;

    [SetUp]
    public void SetUp()
    {
        _mockCodec = new Mock<IPeerInvitationCodec>(MockBehavior.Strict);
        _mockPeerConnectionService = new Mock<IPeerConnectionService>(MockBehavior.Strict);
        _mockRelayDirectory = new Mock<IRelayDirectory>(MockBehavior.Strict);
        _fakeRelayProvisioner = new FakeRelayMailboxProvisioner();
        _fakeClipboardService = new FakeClipboardService();
        _mockPersonaDirectory = new Mock<IPersonaDirectory>(MockBehavior.Strict);

        _testPersonaId = PublicIdentityId.New();
        _testInvitationPayload = new PeerInvitationPayload(
            PublicIdentityId.New(),
            BlindedRoutingToken.New(),
            DeliveryToken.GenerateRandom(),
            new[] { "127.0.0.1" },
            PortRange.Single(5100));
    }

    private AddConnectionDialogViewModel CreateViewModel(bool initializeWithPersona = true)
    {
        var vm = new AddConnectionDialogViewModel(
            _mockCodec.Object,
            _mockPeerConnectionService.Object,
            _mockRelayDirectory.Object,
            _fakeRelayProvisioner,
            _fakeClipboardService,
            _mockPersonaDirectory.Object,
            NullLogger<AddConnectionDialogViewModel>.Instance,
            TimeProvider.System);

        if (initializeWithPersona)
        {
            vm.Initialize(_testPersonaId);
        }

        return vm;
    }

    [Test]
    public void Constructor_WithNullDependencies_ThrowsArgumentNullException()
    {
        var logger = NullLogger<AddConnectionDialogViewModel>.Instance;
        var time = TimeProvider.System;

        Action act1 = () => new AddConnectionDialogViewModel(
            null!, _mockPeerConnectionService.Object, _mockRelayDirectory.Object,
            _fakeRelayProvisioner, _fakeClipboardService, _mockPersonaDirectory.Object, logger, time);
        Action act2 = () => new AddConnectionDialogViewModel(
            _mockCodec.Object, null!, _mockRelayDirectory.Object,
            _fakeRelayProvisioner, _fakeClipboardService, _mockPersonaDirectory.Object, logger, time);
        Action act3 = () => new AddConnectionDialogViewModel(
            _mockCodec.Object, _mockPeerConnectionService.Object, null!,
            _fakeRelayProvisioner, _fakeClipboardService, _mockPersonaDirectory.Object, logger, time);
        Action act4 = () => new AddConnectionDialogViewModel(
            _mockCodec.Object, _mockPeerConnectionService.Object, _mockRelayDirectory.Object,
            null!, _fakeClipboardService, _mockPersonaDirectory.Object, logger, time);
        Action act5 = () => new AddConnectionDialogViewModel(
            _mockCodec.Object, _mockPeerConnectionService.Object, _mockRelayDirectory.Object,
            _fakeRelayProvisioner, null!, _mockPersonaDirectory.Object, logger, time);
        Action act6 = () => new AddConnectionDialogViewModel(
            _mockCodec.Object, _mockPeerConnectionService.Object, _mockRelayDirectory.Object,
            _fakeRelayProvisioner, _fakeClipboardService, null!, logger, time);
        Action act7 = () => new AddConnectionDialogViewModel(
            _mockCodec.Object, _mockPeerConnectionService.Object, _mockRelayDirectory.Object,
            _fakeRelayProvisioner, _fakeClipboardService, _mockPersonaDirectory.Object, null!, time);
        Action act8 = () => new AddConnectionDialogViewModel(
            _mockCodec.Object, _mockPeerConnectionService.Object, _mockRelayDirectory.Object,
            _fakeRelayProvisioner, _fakeClipboardService, _mockPersonaDirectory.Object, logger, null!);

        act1.Should().Throw<ArgumentNullException>();
        act2.Should().Throw<ArgumentNullException>();
        act3.Should().Throw<ArgumentNullException>();
        act4.Should().Throw<ArgumentNullException>();
        act5.Should().Throw<ArgumentNullException>();
        act6.Should().Throw<ArgumentNullException>();
        act7.Should().Throw<ArgumentNullException>();
        act8.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void DefaultState_HasPrekeySeedingDisabled_PerSysPreActivationStealth()
    {
        var vm = CreateViewModel();

        vm.SeedPreKeysToRelay.Should().BeFalse(
            "SYS-PRE-ACTIVATION-STEALTH mandates prekey bundle publication is opt-in and unchecked by default");
        vm.SelectedTabIndex.Should().Be(0);
        vm.IsProbing.Should().BeFalse();
        vm.Result.Should().BeNull();
        vm.ErrorMessage.Should().BeNull();
    }

    [TestCase("percolator://invite?id=123", "invite?id=123")]
    [TestCase("  percolator://invite?id=123  ", "invite?id=123")]
    [TestCase("PERCOLATOR://INVITE?ID=123", "INVITE?ID=123")]
    [TestCase("invite?id=123", "invite?id=123")]
    public void PeerInvitationInput_CleansesPercolatorPrefix_Automatically(string input, string expected)
    {
        var vm = CreateViewModel();

        vm.PeerInvitationInput = input;

        vm.PeerInvitationInput.Should().Be(expected);
    }

    [TestCase("percolator://127.0.0.1:5100", "127.0.0.1:5100")]
    [TestCase("  percolator://127.0.0.1:5100  ", "127.0.0.1:5100")]
    [TestCase("relay://10.0.0.1:5200", "10.0.0.1:5200")]
    [TestCase("  RELAY://10.0.0.1:5200  ", "10.0.0.1:5200")]
    [TestCase("127.0.0.1:5100", "127.0.0.1:5100")]
    public void RelayEndpointInput_CleansesPrefix_Automatically(string input, string expected)
    {
        var vm = CreateViewModel();

        vm.RelayEndpointInput = input;

        vm.RelayEndpointInput.Should().Be(expected);
    }

    [Test]
    public async Task PasteClipboardAsync_RetrievesAndCleansesClipboardText()
    {
        _fakeClipboardService.Text = "  percolator://invite?route=abc&deliv=def  ";

        var vm = CreateViewModel();

        await vm.PasteClipboardCommand.ExecuteAsync(null);

        vm.PeerInvitationInput.Should().Be("invite?route=abc&deliv=def");
    }

    [Test]
    public async Task SubmitPeerConnectionAsync_WhenInputEmpty_SetsErrorMessage()
    {
        var vm = CreateViewModel();
        vm.PeerInvitationInput = "   ";

        await vm.SubmitPeerConnectionCommand.ExecuteAsync(null);

        vm.ErrorMessage.Should().Be("Please enter a peer invitation.");
        vm.Result.Should().BeNull();
    }

    [Test]
    public async Task SubmitPeerConnectionAsync_WhenDecodeFails_SetsErrorMessage()
    {
        _mockCodec
            .Setup(c => c.Decode("corrupted-token"))
            .Returns(DomainResult<PeerInvitationPayload>.Failure(new DomainError("INVALID_INVITATION", "Malformed invitation token.")));

        var vm = CreateViewModel();
        vm.PeerInvitationInput = "corrupted-token";

        await vm.SubmitPeerConnectionCommand.ExecuteAsync(null);

        vm.ErrorMessage.Should().Be("Malformed invitation token.");
        vm.Result.Should().BeNull();
    }

    [Test]
    public async Task SubmitPeerConnectionAsync_WhenDirectPeerConnectSucceeds_CompletesWithConnectedStatus()
    {
        _mockCodec
            .Setup(c => c.Decode("valid-token"))
            .Returns(DomainResult<PeerInvitationPayload>.Success(_testInvitationPayload));

        var expectedResult = new PeerConnectionResult(ChannelId.New(), DeviceId.Primary);

        _mockPeerConnectionService
            .Setup(p => p.ConnectToDirectPeerAsync(
                _testInvitationPayload.IdentityId,
                _testInvitationPayload.RoutingToken,
                _testInvitationPayload.DeliveryToken,
                new Uri("https://127.0.0.1:5100"),
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<PeerConnectionResult>.Success(expectedResult));

        var vm = CreateViewModel();
        vm.PeerInvitationInput = "valid-token";

        var closeRequested = false;
        vm.RequestClose += (_, _) => closeRequested = true;

        await vm.SubmitPeerConnectionCommand.ExecuteAsync(null);

        closeRequested.Should().BeTrue();
        vm.Result.Should().NotBeNull();
        vm.Result!.Status.Should().Be(OutboundConnectionStatus.Connected);
        vm.Result.PeerId.Should().Be(_testInvitationPayload.IdentityId);
        vm.Result.Invitation.Should().Be(_testInvitationPayload);
        vm.ErrorMessage.Should().BeNull();
    }

    [Test]
    public async Task SubmitPeerConnectionAsync_WhenDirectPeerConnectFails_CompletesWithPendingOfflineStatus()
    {
        _mockCodec
            .Setup(c => c.Decode("valid-token"))
            .Returns(DomainResult<PeerInvitationPayload>.Success(_testInvitationPayload));

        _mockPeerConnectionService
            .Setup(p => p.ConnectToDirectPeerAsync(
                _testInvitationPayload.IdentityId,
                _testInvitationPayload.RoutingToken,
                _testInvitationPayload.DeliveryToken,
                new Uri("https://127.0.0.1:5100"),
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<PeerConnectionResult>.Failure(new DomainError("PEER_UNREACHABLE", "Target host refused connection.")));

        var vm = CreateViewModel();
        vm.PeerInvitationInput = "valid-token";

        var closeRequested = false;
        vm.RequestClose += (_, _) => closeRequested = true;

        await vm.SubmitPeerConnectionCommand.ExecuteAsync(null);

        closeRequested.Should().BeTrue("offline peers must not trap the user in a blocking error modal");
        vm.Result.Should().NotBeNull();
        vm.Result!.Status.Should().Be(OutboundConnectionStatus.PendingOffline);
        vm.Result.PeerId.Should().Be(_testInvitationPayload.IdentityId);
        vm.Result.Invitation.Should().Be(_testInvitationPayload);
        vm.ErrorMessage.Should().BeNull("failing the fast offline probe stages background retry without UI error");
    }

    [Test]
    public async Task SubmitPeerConnectionAsync_WhenProbeTimesOut_CompletesWithPendingOfflineStatus()
    {
        _mockCodec
            .Setup(c => c.Decode("valid-token"))
            .Returns(DomainResult<PeerInvitationPayload>.Success(_testInvitationPayload));

        _mockPeerConnectionService
            .Setup(p => p.ConnectToDirectPeerAsync(
                _testInvitationPayload.IdentityId,
                _testInvitationPayload.RoutingToken,
                _testInvitationPayload.DeliveryToken,
                new Uri("https://127.0.0.1:5100"),
                null,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var vm = CreateViewModel();
        vm.PeerInvitationInput = "valid-token";

        var closeRequested = false;
        vm.RequestClose += (_, _) => closeRequested = true;

        await vm.SubmitPeerConnectionCommand.ExecuteAsync(null);

        closeRequested.Should().BeTrue();
        vm.Result.Should().NotBeNull();
        vm.Result!.Status.Should().Be(OutboundConnectionStatus.PendingOffline);
        vm.Result.PeerId.Should().Be(_testInvitationPayload.IdentityId);
        vm.ErrorMessage.Should().BeNull();
    }

    [Test]
    public async Task SubmitPeerConnectionAsync_WithIpv6Host_BuildsValidUriWithoutCrash()
    {
        var ipv6Payload = new PeerInvitationPayload(
            PublicIdentityId.New(),
            BlindedRoutingToken.New(),
            DeliveryToken.GenerateRandom(),
            new[] { "::1" },
            PortRange.Single(5100));

        _mockCodec
            .Setup(c => c.Decode("ipv6-token"))
            .Returns(DomainResult<PeerInvitationPayload>.Success(ipv6Payload));

        _mockPeerConnectionService
            .Setup(p => p.ConnectToDirectPeerAsync(
                ipv6Payload.IdentityId,
                ipv6Payload.RoutingToken,
                ipv6Payload.DeliveryToken,
                new Uri("https://[::1]:5100"),
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<PeerConnectionResult>.Success(new PeerConnectionResult(ChannelId.New(), DeviceId.Primary)));

        var vm = CreateViewModel();
        vm.PeerInvitationInput = "ipv6-token";

        await vm.SubmitPeerConnectionCommand.ExecuteAsync(null);

        vm.Result.Should().NotBeNull();
        vm.Result!.Status.Should().Be(OutboundConnectionStatus.Connected);
    }

    [Test]
    public async Task SubmitRelayConnectionAsync_WhenEndpointEmpty_SetsErrorMessage()
    {
        var vm = CreateViewModel();
        vm.RelayEndpointInput = "   ";

        await vm.SubmitRelayConnectionCommand.ExecuteAsync(null);

        vm.ErrorMessage.Should().Be("Please enter a relay endpoint (host:port).");
        vm.Result.Should().BeNull();
    }

    [TestCase("not-a-port")]
    [TestCase("127.0.0.1:")]
    [TestCase("127.0.0.1:99999")]
    [TestCase("127.0.0.1:0")]
    public async Task SubmitRelayConnectionAsync_WhenEndpointInvalid_SetsErrorMessage(string invalidEndpoint)
    {
        var vm = CreateViewModel();
        vm.RelayEndpointInput = invalidEndpoint;

        await vm.SubmitRelayConnectionCommand.ExecuteAsync(null);

        vm.ErrorMessage.Should().StartWith("Invalid relay endpoint format.");
        vm.Result.Should().BeNull();
    }

    [Test]
    public async Task SubmitRelayConnectionAsync_WhenNoActivePersona_ListsPersonasAndUsesFirst()
    {
        var expectedEndpoint = RelayEndpoint.FromHostAndPort("127.0.0.1", 5100);
        var dummyEntry = new RelayDirectoryEntry(PublicIdentityId.New(), "Relay", expectedEndpoint, false, Array.Empty<PublicIdentityId>());
        var personaEntry = new PersonaDirectoryEntry(_testPersonaId, "Fallback Persona", 5100, null, false, 0);

        _mockPersonaDirectory
            .Setup(p => p.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { personaEntry });

        _mockRelayDirectory
            .Setup(r => r.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<RelayDirectoryEntry>());

        _mockRelayDirectory
            .Setup(r => r.RegisterRemoteRelayAsync(
                It.IsAny<PublicIdentityId>(),
                expectedEndpoint,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<RelayDirectoryEntry>.Success(dummyEntry));

        _mockRelayDirectory
            .Setup(r => r.LinkIdentityAsync(
                It.IsAny<PublicIdentityId>(),
                _testPersonaId,
                null,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<RelayMailboxBinding>.Success(
                new RelayMailboxBinding(_testPersonaId, BlindedRoutingToken.New(), DeliveryToken.GenerateRandom(), DateTimeOffset.UtcNow)));

        _mockRelayDirectory
            .Setup(r => r.EnsureMailboxProvisionedAsync(
                It.IsAny<PublicIdentityId>(),
                _testPersonaId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult.Success());

        var vm = CreateViewModel(initializeWithPersona: false);
        vm.RelayEndpointInput = "127.0.0.1:5100";

        await vm.SubmitRelayConnectionCommand.ExecuteAsync(null);

        vm.Result.Should().NotBeNull();
        vm.Result!.Status.Should().Be(OutboundConnectionStatus.RelayAdded);
    }

    [Test]
    public async Task SubmitRelayConnectionAsync_WhenRelayAlreadyRegistered_ReusesExistingRegistration()
    {
        var expectedEndpoint = RelayEndpoint.FromHostAndPort("127.0.0.1", 5100);
        var existingRelayId = PublicIdentityId.New();
        var existingEntry = new RelayDirectoryEntry(existingRelayId, "Existing Relay", expectedEndpoint, false, Array.Empty<PublicIdentityId>());

        _mockRelayDirectory
            .Setup(r => r.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { existingEntry });

        _mockRelayDirectory
            .Setup(r => r.LinkIdentityAsync(
                existingRelayId,
                _testPersonaId,
                null,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<RelayMailboxBinding>.Success(
                new RelayMailboxBinding(_testPersonaId, BlindedRoutingToken.New(), DeliveryToken.GenerateRandom(), DateTimeOffset.UtcNow)));

        _mockRelayDirectory
            .Setup(r => r.EnsureMailboxProvisionedAsync(
                existingRelayId,
                _testPersonaId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult.Success());

        var vm = CreateViewModel();
        vm.RelayEndpointInput = "127.0.0.1:5100";

        await vm.SubmitRelayConnectionCommand.ExecuteAsync(null);

        vm.Result.Should().NotBeNull();
        vm.Result!.Status.Should().Be(OutboundConnectionStatus.RelayAdded);
        vm.Result.RelayEndpoint.Should().Be(expectedEndpoint.ToString());
    }

    [Test]
    public async Task SubmitRelayConnectionAsync_WhenValidWithoutPrekeyOptIn_DoesNotPublishPrekeys()
    {
        var expectedEndpoint = RelayEndpoint.FromHostAndPort("127.0.0.1", 5100);
        var dummyEntry = new RelayDirectoryEntry(PublicIdentityId.New(), "Relay", expectedEndpoint, false, Array.Empty<PublicIdentityId>());

        _mockRelayDirectory
            .Setup(r => r.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<RelayDirectoryEntry>());

        _mockRelayDirectory
            .Setup(r => r.RegisterRemoteRelayAsync(
                It.IsAny<PublicIdentityId>(),
                expectedEndpoint,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<RelayDirectoryEntry>.Success(dummyEntry));

        _mockRelayDirectory
            .Setup(r => r.LinkIdentityAsync(
                It.IsAny<PublicIdentityId>(),
                _testPersonaId,
                null,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<RelayMailboxBinding>.Success(
                new RelayMailboxBinding(_testPersonaId, BlindedRoutingToken.New(), DeliveryToken.GenerateRandom(), DateTimeOffset.UtcNow)));

        _mockRelayDirectory
            .Setup(r => r.EnsureMailboxProvisionedAsync(
                It.IsAny<PublicIdentityId>(),
                _testPersonaId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult.Success());

        var vm = CreateViewModel();
        vm.RelayEndpointInput = "127.0.0.1:5100";
        vm.SeedPreKeysToRelay.Should().BeFalse();

        var closeRequested = false;
        vm.RequestClose += (_, _) => closeRequested = true;

        await vm.SubmitRelayConnectionCommand.ExecuteAsync(null);

        closeRequested.Should().BeTrue();
        vm.Result.Should().NotBeNull();
        vm.Result!.Status.Should().Be(OutboundConnectionStatus.RelayAdded);
        vm.Result.RelayEndpoint.Should().Be(expectedEndpoint.ToString());
        vm.ErrorMessage.Should().BeNull();

        // State-based assertion on fake provisioner: zero prekeys published
        _fakeRelayProvisioner.PublishedBundles.Should().BeEmpty(
            "prekey bundle publication must remain dormant unless explicitly opted into");
    }

    [Test]
    public async Task SubmitRelayConnectionAsync_WhenPrekeyOptInChecked_PublishesPrekeys()
    {
        var expectedEndpoint = RelayEndpoint.FromHostAndPort("127.0.0.1", 5100);
        var dummyEntry = new RelayDirectoryEntry(PublicIdentityId.New(), "Relay", expectedEndpoint, false, Array.Empty<PublicIdentityId>());
        var triad = new MailboxTokenTriad(BlindedRoutingToken.New(), DeliveryToken.GenerateRandom(), DeliveryToken.GenerateRandom());

        _mockRelayDirectory
            .Setup(r => r.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<RelayDirectoryEntry>());

        _mockRelayDirectory
            .Setup(r => r.RegisterRemoteRelayAsync(
                It.IsAny<PublicIdentityId>(),
                expectedEndpoint,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<RelayDirectoryEntry>.Success(dummyEntry));

        _mockRelayDirectory
            .Setup(r => r.LinkIdentityAsync(
                It.IsAny<PublicIdentityId>(),
                _testPersonaId,
                null,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<RelayMailboxBinding>.Success(
                new RelayMailboxBinding(_testPersonaId, BlindedRoutingToken.New(), DeliveryToken.GenerateRandom(), DateTimeOffset.UtcNow)));

        _mockRelayDirectory
            .Setup(r => r.EnsureMailboxProvisionedAsync(
                It.IsAny<PublicIdentityId>(),
                _testPersonaId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult.Success());

        _mockRelayDirectory
            .Setup(r => r.GetMailboxTokensAsync(
                _testPersonaId,
                It.IsAny<PublicIdentityId>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<MailboxTokenTriad>.Success(triad));

        var vm = CreateViewModel();
        vm.RelayEndpointInput = "127.0.0.1:5100";
        vm.SeedPreKeysToRelay = true;

        var closeRequested = false;
        vm.RequestClose += (_, _) => closeRequested = true;

        await vm.SubmitRelayConnectionCommand.ExecuteAsync(null);

        closeRequested.Should().BeTrue();
        vm.Result.Should().NotBeNull();
        vm.Result!.Status.Should().Be(OutboundConnectionStatus.RelayAdded);
        vm.ErrorMessage.Should().BeNull();

        // State-based assertion on fake provisioner: single published bundle recorded
        _fakeRelayProvisioner.PublishedBundles.Should().ContainSingle();
        var bundle = _fakeRelayProvisioner.PublishedBundles[0];
        bundle.OwnerIdentityId.Should().Be(_testPersonaId);
        bundle.DeviceId.Should().Be(DeviceId.Primary);
        bundle.Endpoint.Should().Be(expectedEndpoint);
        bundle.Tokens.Should().Be(triad);
        bundle.Count.Should().Be(20);
    }

    [Test]
    public async Task SubmitRelayConnectionAsync_WhenRegistrationFails_DisplaysError()
    {
        var expectedEndpoint = RelayEndpoint.FromHostAndPort("127.0.0.1", 5100);

        _mockRelayDirectory
            .Setup(r => r.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<RelayDirectoryEntry>());

        _mockRelayDirectory
            .Setup(r => r.RegisterRemoteRelayAsync(
                It.IsAny<PublicIdentityId>(),
                expectedEndpoint,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<RelayDirectoryEntry>.Failure(new DomainError("TLS_FAULT", "Remote host rejected TLS handshake.")));

        var vm = CreateViewModel();
        vm.RelayEndpointInput = "127.0.0.1:5100";

        var closeRequested = false;
        vm.RequestClose += (_, _) => closeRequested = true;

        await vm.SubmitRelayConnectionCommand.ExecuteAsync(null);

        closeRequested.Should().BeFalse();
        vm.Result.Should().BeNull();
        vm.ErrorMessage.Should().Be("Remote host rejected TLS handshake.");
    }

    [Test]
    public void CancelCommand_SetsCancelledStatusAndRaisesRequestClose()
    {
        var vm = CreateViewModel();

        var closeRequested = false;
        vm.RequestClose += (_, _) => closeRequested = true;

        vm.CancelCommand.Execute(null);

        closeRequested.Should().BeTrue();
        vm.Result.Should().NotBeNull();
        vm.Result!.Status.Should().Be(OutboundConnectionStatus.Cancelled);
    }
}

internal sealed class FakeClipboardService : IClipboardService
{
    public string? Text { get; set; }

    public Task<string?> GetTextAsync() => Task.FromResult(Text);
    public Task SetTextAsync(string text)
    {
        Text = text;
        return Task.CompletedTask;
    }
}

internal sealed class FakeRelayMailboxProvisioner : IRelayMailboxProvisioner
{
    public sealed record PublishedBundle(
        PublicIdentityId OwnerIdentityId,
        DeviceId DeviceId,
        RelayEndpoint Endpoint,
        MailboxTokenTriad Tokens,
        uint Count);

    public List<PublishedBundle> PublishedBundles { get; } = new();

    public Task<DomainResult> ProvisionMailboxAsync(
        PublicIdentityId ownerIdentityId,
        PublicIdentityId relayIdentityId,
        RelayEndpoint endpoint,
        MailboxTokenTriad tokens,
        CancellationToken ct = default)
    {
        return Task.FromResult(DomainResult.Success());
    }

    public Task<DomainResult> PublishPreKeyBundleAsync(
        PublicIdentityId ownerIdentityId,
        DeviceId deviceId,
        RelayEndpoint endpoint,
        MailboxTokenTriad tokens,
        uint newOneTimePreKeyCount,
        CancellationToken ct = default)
    {
        PublishedBundles.Add(new PublishedBundle(ownerIdentityId, deviceId, endpoint, tokens, newOneTimePreKeyCount));
        return Task.FromResult(DomainResult.Success());
    }

    public Task<DomainResult<RelayPreKeyStatus>> GetPreKeyStatusAsync(
        DeviceId deviceId,
        RelayEndpoint endpoint,
        MailboxTokenTriad tokens,
        CancellationToken ct = default)
    {
        return Task.FromResult(DomainResult<RelayPreKeyStatus>.Success(new RelayPreKeyStatus(true, 50, new SignedPreKeyId(1))));
    }
}
