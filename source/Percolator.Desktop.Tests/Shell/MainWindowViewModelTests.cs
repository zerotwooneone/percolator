// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Percolator.Application.Invitations;
using Percolator.Application.Ports;
using Percolator.Application.Session.Ports;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Shell.Models;
using Percolator.Desktop.Features.Shell.ViewModels;
using Percolator.Desktop.Features.Transports.Models;
using Percolator.Desktop.Features.Transports.ViewModels;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;
using R3;

namespace Percolator.Desktop.Tests.Shell;

[TestFixture]
public sealed class MainWindowViewModelTests
{
    private Mock<IWindowManager> _windowManagerMock = null!;
    private Mock<IPeerContactQueryService> _contactQueryMock = null!;
    private Mock<ICurrentPersonaService> _currentPersonaServiceMock = null!;
    private Subject<ActivePersonaModel?> _currentPersonaChanged = null!;
    private PublicIdentityId _activePersonaId;
    private ActivePersonaModel _testPersonaModel = null!;
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

        _windowManagerMock = new Mock<IWindowManager>();
        _contactQueryMock = new Mock<IPeerContactQueryService>();
        _currentPersonaServiceMock = new Mock<ICurrentPersonaService>();
        _currentPersonaChanged = new Subject<ActivePersonaModel?>();

        _activePersonaId = new PublicIdentityId(Guid.NewGuid());
        _testPersonaModel = new ActivePersonaModel(_activePersonaId, "Alice", 5124);

        _currentPersonaServiceMock.Setup(s => s.CurrentPersona).Returns(_testPersonaModel);
        _currentPersonaServiceMock.Setup(s => s.CurrentPersonaChanged).Returns(_currentPersonaChanged);
    }

    [TearDown]
    public void TearDown()
    {
        SynchronizationContext.SetSynchronizationContext(_previousSyncContext);
        _currentPersonaChanged.Dispose();
    }

    private MainWindowViewModel CreateViewModel()
    {
        return new MainWindowViewModel(
            _windowManagerMock.Object,
            _contactQueryMock.Object,
            _currentPersonaServiceMock.Object);
    }

    [Test]
    public void Constructor_WithNullDependencies_ThrowsArgumentNullException()
    {
        var act1 = () => new MainWindowViewModel(null!, _contactQueryMock.Object, _currentPersonaServiceMock.Object);
        act1.Should().Throw<ArgumentNullException>().WithParameterName("windowManager");

        var act2 = () => new MainWindowViewModel(_windowManagerMock.Object, null!, _currentPersonaServiceMock.Object);
        act2.Should().Throw<ArgumentNullException>().WithParameterName("contactQueryService");

        var act3 = () => new MainWindowViewModel(_windowManagerMock.Object, _contactQueryMock.Object, null!);
        act3.Should().Throw<ArgumentNullException>().WithParameterName("currentPersonaService");
    }

    [Test]
    public void Constructor_SeedsCurrentPersonaFromService()
    {
        using var vm = CreateViewModel();

        vm.CurrentPersona.Should().NotBeNull();
        vm.CurrentPersona!.IdentityId.Should().Be(_activePersonaId);
        vm.CurrentPersona.DisplayName.Should().Be("Alice");
        vm.CurrentPersona.BoundPort.Should().Be(5124);
        vm.HasCurrentPersona.Should().BeTrue();
    }

    [Test]
    public void CurrentPersonaChanged_UpdatesViewModelAndRefreshesPendingCount()
    {
        using var vm = CreateViewModel();

        var newPersonaId = new PublicIdentityId(Guid.NewGuid());
        var newPersona = new ActivePersonaModel(newPersonaId, "Bob", 5126);

        _contactQueryMock
            .Setup(q => q.GetContactSummariesAsync(newPersonaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PeerContactSummaryReadModel>
            {
                new(new PublicIdentityId(Guid.NewGuid()), "Charlie", PeerTrustLevel.Untrusted, ContactState.PendingApproval, DateTimeOffset.UtcNow, false)
            });

        _currentPersonaChanged.OnNext(newPersona);

        vm.CurrentPersona.Should().Be(newPersona);
        vm.PendingRequestCount.Should().Be(1);
        vm.HasPendingRequests.Should().BeTrue();
    }

    [Test]
    public void PendingRequestCount_NotifiesHasPendingRequests_WhenCrossingZeroBoundary()
    {
        using var vm = CreateViewModel();
        vm.HasPendingRequests.Should().BeFalse();

        var notified = new List<string?>();
        vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        vm.PendingRequestCount = 2;
        vm.HasPendingRequests.Should().BeTrue("the indicator light must show staged inbound handshakes");
        notified.Should().Contain(nameof(MainWindowViewModel.HasPendingRequests),
            "the indicator binding must be notified when the count leaves zero");

        notified.Clear();
        vm.PendingRequestCount = 0;
        vm.HasPendingRequests.Should().BeFalse();
        notified.Should().Contain(nameof(MainWindowViewModel.HasPendingRequests),
            "the indicator binding must be notified when the count returns to zero");
    }

    [Test]
    public async Task OpenAddConnectionCommand_OpensAddConnectionDialogViaWindowManager()
    {
        using var vm = CreateViewModel();
        PublicIdentityId capturedPersonaId = default;

        _windowManagerMock
            .Setup(w => w.ShowDialogAsync<AddConnectionDialogViewModel, PublicIdentityId, AddConnectionDialogResult>(
                It.IsAny<PublicIdentityId>(), null))
            .Callback<PublicIdentityId, Avalonia.Controls.Window?>((id, _) =>
            {
                capturedPersonaId = id;
            })
            .ReturnsAsync(new AddConnectionDialogResult(OutboundConnectionStatus.Connected));

        await vm.OpenAddConnectionCommand.ExecuteAsync(null);

        capturedPersonaId.Should().Be(_activePersonaId, "Add Connection dialog must be configured with active persona");
    }

    [Test]
    public async Task OpenPendingRequestsCommand_OpensPendingRequestsDialogViaWindowManager()
    {
        using var vm = CreateViewModel();
        PublicIdentityId capturedPersonaId = default;

        _windowManagerMock
            .Setup(w => w.ShowDialogAsync<PendingRequestsPanelViewModel, PublicIdentityId>(
                It.IsAny<PublicIdentityId>(), null))
            .Callback<PublicIdentityId, Avalonia.Controls.Window?>((id, _) =>
            {
                capturedPersonaId = id;
            })
            .Returns(Task.CompletedTask);

        _contactQueryMock
            .Setup(q => q.GetContactSummariesAsync(_activePersonaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PeerContactSummaryReadModel>());

        await vm.OpenPendingRequestsCommand.ExecuteAsync(null);

        capturedPersonaId.Should().Be(_activePersonaId, "Pending Requests panel must be configured with active persona");
    }

    [Test]
    public async Task OpenShareInvitationCommand_ConfiguresShareInvitationDialogWithPersonaParameters()
    {
        using var vm = CreateViewModel();
        ShareInvitationDialogParams? capturedParams = null;

        _windowManagerMock
            .Setup(w => w.ShowDialogAsync<ShareInvitationDialogViewModel, ShareInvitationDialogParams>(
                It.IsAny<ShareInvitationDialogParams>(), null))
            .Callback<ShareInvitationDialogParams, Avalonia.Controls.Window?>((p, _) =>
            {
                capturedParams = p;
            })
            .Returns(Task.CompletedTask);

        await vm.OpenShareInvitationCommand.ExecuteAsync(null);

        capturedParams.Should().NotBeNull("Share Invitation dialog must be configured with active persona parameters");
        capturedParams!.IdentityId.Should().Be(_activePersonaId);
        capturedParams.BoundPort.Should().Be(5124, "bound port from active persona model must be passed to invitation dialog");
    }

    [Test]
    public async Task RefreshPendingCountAsync_WithPendingApprovals_UpdatesCountAndHasPendingFlag()
    {
        using var vm = CreateViewModel();

        _contactQueryMock
            .Setup(q => q.GetContactSummariesAsync(_activePersonaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PeerContactSummaryReadModel>
            {
                new(new PublicIdentityId(Guid.NewGuid()), "Bob", PeerTrustLevel.Untrusted, ContactState.PendingApproval, DateTimeOffset.UtcNow, false),
                new(new PublicIdentityId(Guid.NewGuid()), "Charlie", PeerTrustLevel.Untrusted, ContactState.PendingApproval, DateTimeOffset.UtcNow, false),
                new(new PublicIdentityId(Guid.NewGuid()), "Dave", PeerTrustLevel.Verified, ContactState.Active, DateTimeOffset.UtcNow, true)
            });

        await vm.RefreshPendingCountAsync();

        vm.PendingRequestCount.Should().Be(2);
        vm.HasPendingRequests.Should().BeTrue();
    }

    [Test]
    public async Task RefreshPendingCountAsync_WithNoPending_HasPendingRequestsIsFalse()
    {
        using var vm = CreateViewModel();

        _contactQueryMock
            .Setup(q => q.GetContactSummariesAsync(_activePersonaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PeerContactSummaryReadModel>
            {
                new(new PublicIdentityId(Guid.NewGuid()), "Dave", PeerTrustLevel.Verified, ContactState.Active, DateTimeOffset.UtcNow, true)
            });

        await vm.RefreshPendingCountAsync();

        vm.PendingRequestCount.Should().Be(0);
        vm.HasPendingRequests.Should().BeFalse();
    }

    [Test]
    public void ToggleInspector_InvertsIsInspectorOpenState()
    {
        using var vm = CreateViewModel();
        vm.IsInspectorOpen.Should().BeFalse();

        vm.ToggleInspectorCommand.Execute(null);
        vm.IsInspectorOpen.Should().BeTrue();

        vm.ToggleInspectorCommand.Execute(null);
        vm.IsInspectorOpen.Should().BeFalse();
    }

    [Test]
    public void ToggleChatsSidebar_InvertsIsChatsSidebarOpenState()
    {
        using var vm = CreateViewModel();
        vm.IsChatsSidebarOpen.Should().BeTrue();

        vm.ToggleChatsSidebarCommand.Execute(null);
        vm.IsChatsSidebarOpen.Should().BeFalse();

        vm.ToggleChatsSidebarCommand.Execute(null);
        vm.IsChatsSidebarOpen.Should().BeTrue();
    }
}
