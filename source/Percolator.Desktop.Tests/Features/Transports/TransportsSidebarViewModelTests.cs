// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

namespace Percolator.Desktop.Tests.Features.Transports;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using ObservableCollections;
using Percolator.Application.Session.Ports;
using Percolator.Desktop.Common.Navigation;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Shell.Models;
using Percolator.Desktop.Features.Transports.Models;
using Percolator.Desktop.Features.Transports.Services;
using Percolator.Desktop.Features.Transports.ViewModels;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using R3;

[TestFixture]
public sealed class TransportsSidebarViewModelTests
{
    private ShellNavigationStateService _navigationService = null!;
    private Mock<IRelayStateService> _relayStateServiceMock = null!;
    private FakeInboundRequestStateService _fakeInboundService = null!;
    private Mock<ICurrentPersonaService> _currentPersonaServiceMock = null!;
    private Mock<IWindowManager> _windowManagerMock = null!;
    private Subject<ActivePersonaModel?> _currentPersonaChanged = null!;
    private PublicIdentityId _personaId;
    private ActivePersonaModel _personaModel = null!;
    private ObservableList<RelayChannelModel> _relayList = null!;
    private SynchronizationContext? _previousSyncContext;

    private sealed class ImmediateSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    [SetUp]
    public void SetUp()
    {
        _previousSyncContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new ImmediateSynchronizationContext());

        _navigationService = new ShellNavigationStateService(NullLogger<ShellNavigationStateService>.Instance);
        _relayStateServiceMock = new Mock<IRelayStateService>();
        _fakeInboundService = new FakeInboundRequestStateService();
        _currentPersonaServiceMock = new Mock<ICurrentPersonaService>();
        _windowManagerMock = new Mock<IWindowManager>();
        _currentPersonaChanged = new Subject<ActivePersonaModel?>();

        _personaId = PublicIdentityId.New();
        _personaModel = new ActivePersonaModel(_personaId, "Alice", 5124);

        _currentPersonaServiceMock.SetupGet(c => c.CurrentPersona).Returns(_personaModel);
        _currentPersonaServiceMock.SetupGet(c => c.CurrentPersonaChanged).Returns(_currentPersonaChanged);

        _relayList = new ObservableList<RelayChannelModel>();
        var relayView = _relayList.CreateView(r => r);
        _relayStateServiceMock.SetupGet(r => r.Relays).Returns(relayView);
    }

    [TearDown]
    public void TearDown()
    {
        SynchronizationContext.SetSynchronizationContext(_previousSyncContext);
        _navigationService.Dispose();
        _fakeInboundService.Dispose();
        _currentPersonaChanged.Dispose();
    }

    private TransportsSidebarViewModel CreateViewModel()
    {
        return new TransportsSidebarViewModel(
            _navigationService,
            _relayStateServiceMock.Object,
            _fakeInboundService,
            _currentPersonaServiceMock.Object,
            _windowManagerMock.Object,
            NullLogger<TransportsSidebarViewModel>.Instance);
    }

    [Test]
    public void Constructor_WithNullDependencies_ThrowsArgumentNullException()
    {
        var logger = NullLogger<TransportsSidebarViewModel>.Instance;

        Action act1 = () => new TransportsSidebarViewModel(null!, _relayStateServiceMock.Object, _fakeInboundService, _currentPersonaServiceMock.Object, _windowManagerMock.Object, logger);
        Action act2 = () => new TransportsSidebarViewModel(_navigationService, null!, _fakeInboundService, _currentPersonaServiceMock.Object, _windowManagerMock.Object, logger);
        Action act3 = () => new TransportsSidebarViewModel(_navigationService, _relayStateServiceMock.Object, null!, _currentPersonaServiceMock.Object, _windowManagerMock.Object, logger);
        Action act4 = () => new TransportsSidebarViewModel(_navigationService, _relayStateServiceMock.Object, _fakeInboundService, null!, _windowManagerMock.Object, logger);
        Action act5 = () => new TransportsSidebarViewModel(_navigationService, _relayStateServiceMock.Object, _fakeInboundService, _currentPersonaServiceMock.Object, null!, logger);
        Action act6 = () => new TransportsSidebarViewModel(_navigationService, _relayStateServiceMock.Object, _fakeInboundService, _currentPersonaServiceMock.Object, _windowManagerMock.Object, null!);

        act1.Should().Throw<ArgumentNullException>();
        act2.Should().Throw<ArgumentNullException>();
        act3.Should().Throw<ArgumentNullException>();
        act4.Should().Throw<ArgumentNullException>();
        act5.Should().Throw<ArgumentNullException>();
        act6.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void InitialState_SelectsDirectMessages()
    {
        using var vm = CreateViewModel();

        vm.IsDirectMessagesActive.Should().BeTrue();
        vm.ActiveTarget.CurrentValue.Should().Be(LeftRailTarget.DirectMessages);
        vm.SelectedRelay.Should().BeNull();
        vm.HasCurrentPersona.Should().BeTrue();
        vm.HasPendingRequests.Should().BeFalse();
        vm.PendingRequestsCount.Should().Be(0);
    }

    [Test]
    public void SelectRelayCommand_SwitchesActiveLeftRailTargetToRelay()
    {
        var relayId = PublicIdentityId.New();
        var ep = RelayEndpoint.Parse("percolator://relay.net");
        var relayModel = new RelayChannelModel(relayId, "Relay Alpha", ep, RelayConnectionStatus.Connected);
        _relayList.Add(relayModel);

        using var vm = CreateViewModel();

        vm.SelectRelayCommand.Execute(relayId);

        vm.IsDirectMessagesActive.Should().BeFalse();
        vm.ActiveTarget.CurrentValue.Should().Be(LeftRailTarget.Relay(relayId));
        vm.SelectedRelay.Should().Be(relayModel);
    }

    [Test]
    public void SelectDirectMessagesCommand_SwitchesActiveLeftRailTargetToDirectMessages()
    {
        var relayId = PublicIdentityId.New();
        var ep = RelayEndpoint.Parse("percolator://relay.net");
        var relayModel = new RelayChannelModel(relayId, "Relay Alpha", ep, RelayConnectionStatus.Connected);
        _relayList.Add(relayModel);

        using var vm = CreateViewModel();
        vm.SelectRelayCommand.Execute(relayId);

        vm.SelectDirectMessagesCommand.Execute(null);

        vm.IsDirectMessagesActive.Should().BeTrue();
        vm.ActiveTarget.CurrentValue.Should().Be(LeftRailTarget.DirectMessages);
        vm.SelectedRelay.Should().BeNull();
    }

    [Test]
    public void PendingRequests_IlluminatesIndicatorLight()
    {
        using var vm = CreateViewModel();
        vm.HasPendingRequests.Should().BeFalse();

        _fakeInboundService.BackingList.Add(new InboundPendingRequestModel(
            PublicIdentityId.New(), "Peer Bob", "1234...5678", DateTimeOffset.UtcNow));

        vm.HasPendingRequests.Should().BeTrue();
        vm.PendingRequestsCount.Should().Be(1);

        _fakeInboundService.BackingList.Clear();

        vm.HasPendingRequests.Should().BeFalse();
        vm.PendingRequestsCount.Should().Be(0);
    }

    [Test]
    public async Task OpenAddConnectionCommand_WhenRelayAdded_RegistersRelayInRelayStateService()
    {
        using var vm = CreateViewModel();
        string? addedEndpoint = null;
        PublicIdentityId addedPersonaId = default;

        _windowManagerMock
            .Setup(w => w.ShowDialogAsync<AddConnectionDialogViewModel, PublicIdentityId, AddConnectionDialogResult>(
                _personaId, null))
            .ReturnsAsync(new AddConnectionDialogResult(OutboundConnectionStatus.RelayAdded, RelayEndpoint: "percolator://relay.org"));

        _relayStateServiceMock
            .Setup(r => r.AddRelayAsync(It.IsAny<PublicIdentityId>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<PublicIdentityId, string, string?, CancellationToken>((id, ep, _, _) =>
            {
                addedPersonaId = id;
                addedEndpoint = ep;
            })
            .ReturnsAsync(new RelayChannelModel(PublicIdentityId.New(), "relay.org", RelayEndpoint.Parse("percolator://relay.org")));

        await vm.OpenAddConnectionCommand.ExecuteAsync(null);

        addedEndpoint.Should().Be("percolator://relay.org");
        addedPersonaId.Should().Be(_personaId);
    }

    [Test]
    public async Task OpenPendingRequestsCommand_OpensDialogViaWindowManager()
    {
        using var vm = CreateViewModel();
        PublicIdentityId dialogPersonaId = default;

        _windowManagerMock
            .Setup(w => w.ShowDialogAsync<PendingRequestsPanelViewModel, PublicIdentityId>(
                _personaId, null))
            .Callback<PublicIdentityId, Avalonia.Controls.Window?>((id, _) => dialogPersonaId = id)
            .Returns(Task.CompletedTask);

        await vm.OpenPendingRequestsCommand.ExecuteAsync(null);

        dialogPersonaId.Should().Be(_personaId);
    }

    [Test]
    public async Task OpenShareInvitationCommand_OpensDialogViaWindowManager()
    {
        using var vm = CreateViewModel();
        ShareInvitationDialogParams? capturedParams = null;

        _windowManagerMock
            .Setup(w => w.ShowDialogAsync<ShareInvitationDialogViewModel, ShareInvitationDialogParams>(
                It.IsAny<ShareInvitationDialogParams>(), null))
            .Callback<ShareInvitationDialogParams, Avalonia.Controls.Window?>((p, _) => capturedParams = p)
            .Returns(Task.CompletedTask);

        await vm.OpenShareInvitationCommand.ExecuteAsync(null);

        capturedParams.Should().NotBeNull();
        capturedParams!.IdentityId.Should().Be(_personaId);
        capturedParams.BoundPort.Should().Be(5124);
    }
}
