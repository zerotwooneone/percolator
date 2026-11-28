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
using IOutboundConnectionWorkflow = Percolator.Application.Connections.IOutboundConnectionWorkflow;
using OutboundConnectionResult = Percolator.Application.Connections.OutboundConnectionResult;
using AppOutboundStatus = Percolator.Application.Connections.OutboundConnectionStatus;
using Percolator.Application.Delivery.Ports;
using Percolator.Application.Invitations;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Startup.Models;
using Percolator.Desktop.Features.Transports.Models;
using Percolator.Desktop.Features.Transports.ViewModels;
using Percolator.Desktop.Tests.Features.Chats;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Model;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Desktop.Tests.Features.Transports;

[TestFixture]
public sealed class AddConnectionDialogViewModelTests
{
    private Mock<IPeerInvitationCodec> _mockCodec = null!;
    private Mock<IOutboundConnectionWorkflow> _mockOutboundWorkflow = null!;
    private Mock<IRelayDirectory> _mockRelayDirectory = null!;
    private FakeRelayMailboxProvisioner _fakeRelayProvisioner = null!;
    private FakeClipboardService _fakeClipboardService = null!;
    private Mock<IPersonaDirectory> _mockPersonaDirectory = null!;
    private FakeConversationStateService _fakeConversationService = null!;

    private PublicIdentityId _testPersonaId;
    private PeerInvitationPayload _testInvitationPayload = null!;
    private Mock<IDialogContext<PublicIdentityId, AddConnectionDialogResult>> _contextMock = null!;
    private AddConnectionDialogResult? _dialogResult;

    [SetUp]
    public void SetUp()
    {
        _mockCodec = new Mock<IPeerInvitationCodec>(MockBehavior.Strict);
        _mockOutboundWorkflow = new Mock<IOutboundConnectionWorkflow>(MockBehavior.Strict);
        _mockRelayDirectory = new Mock<IRelayDirectory>(MockBehavior.Strict);
        _fakeRelayProvisioner = new FakeRelayMailboxProvisioner();
        _fakeClipboardService = new FakeClipboardService();
        _mockPersonaDirectory = new Mock<IPersonaDirectory>(MockBehavior.Strict);
        _fakeConversationService = new FakeConversationStateService();

        _testPersonaId = PublicIdentityId.New();
        _testInvitationPayload = new PeerInvitationPayload(
            PublicIdentityId.New(),
            IdentityKey.FromBytes(new byte[32]),
            RelayEndpoint.Parse("127.0.0.1:5100"),
            BlindedRoutingToken.New());

        _dialogResult = null;
        _contextMock = new Mock<IDialogContext<PublicIdentityId, AddConnectionDialogResult>>();
        _contextMock.SetupGet(c => c.Data).Returns(_testPersonaId);
        _contextMock.Setup(c => c.Close(It.IsAny<AddConnectionDialogResult>()))
            .Callback<AddConnectionDialogResult>(r => _dialogResult = r);
        _contextMock.Setup(c => c.Cancel())
            .Callback(() => _dialogResult = new AddConnectionDialogResult(OutboundConnectionStatus.Cancelled));
    }

    [TearDown]
    public void TearDown()
    {
        _fakeConversationService.Dispose();
    }

    private AddConnectionDialogViewModel CreateViewModel(bool initializeWithPersona = true)
    {
        _dialogResult = null;
        if (!initializeWithPersona)
        {
            _contextMock = new Mock<IDialogContext<PublicIdentityId, AddConnectionDialogResult>>();
            _contextMock.SetupGet(c => c.Data).Returns((PublicIdentityId)default);
            _contextMock.Setup(c => c.Close(It.IsAny<AddConnectionDialogResult>()))
                .Callback<AddConnectionDialogResult>(r => _dialogResult = r);
            _contextMock.Setup(c => c.Cancel())
                .Callback(() => _dialogResult = new AddConnectionDialogResult(OutboundConnectionStatus.Cancelled));
        }

        var vm = new AddConnectionDialogViewModel(
            _contextMock.Object,
            _mockCodec.Object,
            _mockOutboundWorkflow.Object,
            _mockRelayDirectory.Object,
            _fakeRelayProvisioner,
            _fakeClipboardService,
            _mockPersonaDirectory.Object,
            _fakeConversationService,
            NullLogger<AddConnectionDialogViewModel>.Instance,
            TimeProvider.System);

        return vm;
    }

    [Test]
    public void Constructor_WithNullDependencies_ThrowsArgumentNullException()
    {
        var logger = NullLogger<AddConnectionDialogViewModel>.Instance;
        var time = TimeProvider.System;
        var contextMock = new Mock<IDialogContext<PublicIdentityId, AddConnectionDialogResult>>();
        contextMock.SetupGet(c => c.Data).Returns(_testPersonaId);

        Action act0 = () => new AddConnectionDialogViewModel(
            null!, _mockCodec.Object, _mockOutboundWorkflow.Object, _mockRelayDirectory.Object,
            _fakeRelayProvisioner, _fakeClipboardService, _mockPersonaDirectory.Object, _fakeConversationService, logger, time);
        Action act1 = () => new AddConnectionDialogViewModel(
            contextMock.Object, null!, _mockOutboundWorkflow.Object, _mockRelayDirectory.Object,
            _fakeRelayProvisioner, _fakeClipboardService, _mockPersonaDirectory.Object, _fakeConversationService, logger, time);
        Action act2 = () => new AddConnectionDialogViewModel(
            contextMock.Object, _mockCodec.Object, null!, _mockRelayDirectory.Object,
            _fakeRelayProvisioner, _fakeClipboardService, _mockPersonaDirectory.Object, _fakeConversationService, logger, time);
        Action act3 = () => new AddConnectionDialogViewModel(
            contextMock.Object, _mockCodec.Object, _mockOutboundWorkflow.Object, null!,
            _fakeRelayProvisioner, _fakeClipboardService, _mockPersonaDirectory.Object, _fakeConversationService, logger, time);
        Action act4 = () => new AddConnectionDialogViewModel(
            contextMock.Object, _mockCodec.Object, _mockOutboundWorkflow.Object, _mockRelayDirectory.Object,
            null!, _fakeClipboardService, _mockPersonaDirectory.Object, _fakeConversationService, logger, time);
        Action act5 = () => new AddConnectionDialogViewModel(
            contextMock.Object, _mockCodec.Object, _mockOutboundWorkflow.Object, _mockRelayDirectory.Object,
            _fakeRelayProvisioner, null!, _mockPersonaDirectory.Object, _fakeConversationService, logger, time);
        Action act6 = () => new AddConnectionDialogViewModel(
            contextMock.Object, _mockCodec.Object, _mockOutboundWorkflow.Object, _mockRelayDirectory.Object,
            _fakeRelayProvisioner, _fakeClipboardService, null!, _fakeConversationService, logger, time);
        Action act7 = () => new AddConnectionDialogViewModel(
            contextMock.Object, _mockCodec.Object, _mockOutboundWorkflow.Object, _mockRelayDirectory.Object,
            _fakeRelayProvisioner, _fakeClipboardService, _mockPersonaDirectory.Object, null!, logger, time);
        Action act8 = () => new AddConnectionDialogViewModel(
            contextMock.Object, _mockCodec.Object, _mockOutboundWorkflow.Object, _mockRelayDirectory.Object,
            _fakeRelayProvisioner, _fakeClipboardService, _mockPersonaDirectory.Object, _fakeConversationService, null!, time);
        Action act9 = () => new AddConnectionDialogViewModel(
            contextMock.Object, _mockCodec.Object, _mockOutboundWorkflow.Object, _mockRelayDirectory.Object,
            _fakeRelayProvisioner, _fakeClipboardService, _mockPersonaDirectory.Object, _fakeConversationService, logger, null!);

        act0.Should().Throw<ArgumentNullException>();
        act1.Should().Throw<ArgumentNullException>();
        act2.Should().Throw<ArgumentNullException>();
        act3.Should().Throw<ArgumentNullException>();
        act4.Should().Throw<ArgumentNullException>();
        act5.Should().Throw<ArgumentNullException>();
        act6.Should().Throw<ArgumentNullException>();
        act7.Should().Throw<ArgumentNullException>();
        act8.Should().Throw<ArgumentNullException>();
        act9.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void DefaultState_HasPrekeySeedingDisabled_PerSysPreActivationStealth()
    {
        var vm = CreateViewModel();

        vm.SeedPreKeysToRelay.Should().BeFalse(
            "SYS-PRE-ACTIVATION-STEALTH mandates prekey bundle publication is opt-in and unchecked by default");
        vm.SelectedTabIndex.Should().Be(0);
        vm.IsProbing.Should().BeFalse();
        _dialogResult.Should().BeNull();
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
    [TestCase("percolator://10.0.0.1:5100", "10.0.0.1:5100")]
    [TestCase("  PERCOLATOR://10.0.0.1:5100  ", "10.0.0.1:5100")]
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
        _dialogResult.Should().BeNull();
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
        _dialogResult.Should().BeNull();
    }

    [Test]
    public async Task SubmitPeerConnectionAsync_WhenDirectPeerConnectSucceeds_CompletesWithConnectedStatus()
    {
        _mockCodec
            .Setup(c => c.Decode("valid-token"))
            .Returns(DomainResult<PeerInvitationPayload>.Success(_testInvitationPayload));

        var workflowResult = new OutboundConnectionResult(
            AppOutboundStatus.Connected,
            _testInvitationPayload.IdentityId,
            _testInvitationPayload,
            new Uri("https://127.0.0.1:5100"));

        _mockOutboundWorkflow
            .Setup(p => p.ConnectAsync(
                _testPersonaId,
                _testInvitationPayload,
                TimeSpan.FromSeconds(3),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<OutboundConnectionResult>.Success(workflowResult));

        var vm = CreateViewModel();
        vm.PeerInvitationInput = "valid-token";

        await vm.SubmitPeerConnectionCommand.ExecuteAsync(null);

        _dialogResult.Should().NotBeNull();
        _dialogResult!.Status.Should().Be(OutboundConnectionStatus.Connected);
        _dialogResult.PeerId.Should().Be(_testInvitationPayload.IdentityId);
        _dialogResult.Invitation.Should().Be(_testInvitationPayload);
        vm.ErrorMessage.Should().BeNull();
    }

    [Test]
    public async Task SubmitPeerConnectionAsync_WhenDirectPeerConnectFails_CompletesWithPendingOfflineStatus()
    {
        _mockCodec
            .Setup(c => c.Decode("valid-token"))
            .Returns(DomainResult<PeerInvitationPayload>.Success(_testInvitationPayload));

        var workflowResult = new OutboundConnectionResult(
            AppOutboundStatus.PendingOffline,
            _testInvitationPayload.IdentityId,
            _testInvitationPayload,
            new Uri("https://127.0.0.1:5100"));

        _mockOutboundWorkflow
            .Setup(p => p.ConnectAsync(
                _testPersonaId,
                _testInvitationPayload,
                TimeSpan.FromSeconds(3),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<OutboundConnectionResult>.Success(workflowResult));

        var vm = CreateViewModel();
        vm.PeerInvitationInput = "valid-token";

        await vm.SubmitPeerConnectionCommand.ExecuteAsync(null);

        _dialogResult.Should().NotBeNull("offline peers must not trap the user in a blocking error modal");
        _dialogResult!.Status.Should().Be(OutboundConnectionStatus.PendingOffline);
        _dialogResult.PeerId.Should().Be(_testInvitationPayload.IdentityId);
        _dialogResult.Invitation.Should().Be(_testInvitationPayload);
        vm.ErrorMessage.Should().BeNull("failing the fast offline probe stages background retry without UI error");
        _fakeConversationService.StagedPending.Should().ContainSingle(p => p.PeerId == _testInvitationPayload.IdentityId);
    }

    [Test]
    public async Task SubmitPeerConnectionAsync_WhenWorkflowFails_SetsErrorMessage()
    {
        _mockCodec
            .Setup(c => c.Decode("invalid-endpoint-token"))
            .Returns(DomainResult<PeerInvitationPayload>.Success(_testInvitationPayload));

        _mockOutboundWorkflow
            .Setup(p => p.ConnectAsync(
                _testPersonaId,
                _testInvitationPayload,
                TimeSpan.FromSeconds(3),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DomainResult<OutboundConnectionResult>.Failure(new DomainError("INVALID_ENDPOINT", "Invalid endpoint format")));

        var vm = CreateViewModel();
        vm.PeerInvitationInput = "invalid-endpoint-token";

        await vm.SubmitPeerConnectionCommand.ExecuteAsync(null);

        _dialogResult.Should().BeNull();
        vm.ErrorMessage.Should().Be("Invalid endpoint format");
    }

    [Test]
    public async Task SubmitRelayConnectionAsync_WhenEndpointEmpty_SetsErrorMessage()
    {
        var vm = CreateViewModel();
        vm.RelayEndpointInput = "   ";

        await vm.SubmitRelayConnectionCommand.ExecuteAsync(null);

        vm.ErrorMessage.Should().Be("Please enter a relay endpoint (host:port).");
        _dialogResult.Should().BeNull();
    }

    [TestCase("not-a-port")]
    [TestCase("127.0.0.1:")]
    [TestCase("127.0.0.1:99999")]
    [TestCase("127.0.0.1:0")]
    [TestCase("relay://127.0.0.1:5100")]
    [TestCase("https://127.0.0.1:5100")]
    [TestCase("http://127.0.0.1:5100")]
    public async Task SubmitRelayConnectionAsync_WhenEndpointInvalid_SetsErrorMessage(string invalidEndpoint)
    {
        var vm = CreateViewModel();
        vm.RelayEndpointInput = invalidEndpoint;

        await vm.SubmitRelayConnectionCommand.ExecuteAsync(null);

        vm.ErrorMessage.Should().StartWith("Invalid relay endpoint format.");
        _dialogResult.Should().BeNull();
    }

    [Test]
    public void RelayEndpointInput_WhenPercolatorSchemeEntered_StripsSchemePrefix()
    {
        var vm = CreateViewModel();
        vm.RelayEndpointInput = "percolator://127.0.0.1:5100";

        vm.RelayEndpointInput.Should().Be("127.0.0.1:5100");
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

        _dialogResult.Should().NotBeNull();
        _dialogResult!.Status.Should().Be(OutboundConnectionStatus.RelayAdded);
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

        _dialogResult.Should().NotBeNull();
        _dialogResult!.Status.Should().Be(OutboundConnectionStatus.RelayAdded);
        _dialogResult.RelayEndpoint.Should().Be(expectedEndpoint.ToString());
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

        await vm.SubmitRelayConnectionCommand.ExecuteAsync(null);

        _dialogResult.Should().NotBeNull();
        _dialogResult!.Status.Should().Be(OutboundConnectionStatus.RelayAdded);
        _dialogResult.RelayEndpoint.Should().Be(expectedEndpoint.ToString());
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

        await vm.SubmitRelayConnectionCommand.ExecuteAsync(null);

        _dialogResult.Should().NotBeNull();
        _dialogResult!.Status.Should().Be(OutboundConnectionStatus.RelayAdded);
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

        await vm.SubmitRelayConnectionCommand.ExecuteAsync(null);

        _dialogResult.Should().BeNull();
        vm.ErrorMessage.Should().Be("Remote host rejected TLS handshake.");
    }

    [Test]
    public void CancelCommand_SetsCancelledStatusAndClosesDialog()
    {
        var vm = CreateViewModel();

        vm.CancelCommand.Execute(null);

        _dialogResult.Should().NotBeNull();
        _dialogResult!.Status.Should().Be(OutboundConnectionStatus.Cancelled);
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
