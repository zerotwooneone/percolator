// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

namespace Percolator.Desktop.Tests.Shell;

using System;
using System.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using ObservableCollections;
using Percolator.Desktop.Common.Navigation;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Shell.Models;
using Percolator.Desktop.Features.Shell.ViewModels;
using Percolator.Desktop.Features.Transports.Models;
using Percolator.Desktop.Features.Transports.Services;
using Percolator.Desktop.Features.Transports.ViewModels;
using Percolator.Desktop.Tests.Features.Transports;
using Percolator.Domain.Identities;
using R3;

[TestFixture]
public sealed class MainWindowViewModelTests
{
    private Mock<ICurrentPersonaService> _currentPersonaServiceMock = null!;
    private Mock<IWindowManager> _windowManagerMock = null!;
    private Mock<IRelayStateService> _relayStateServiceMock = null!;
    private FakeInboundRequestStateService _fakeInboundService = null!;
    private TransportsSidebarViewModel _transportsSidebar = null!;
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

        _currentPersonaServiceMock = new Mock<ICurrentPersonaService>();
        _windowManagerMock = new Mock<IWindowManager>();
        _relayStateServiceMock = new Mock<IRelayStateService>();
        _fakeInboundService = new FakeInboundRequestStateService();
        _currentPersonaChanged = new Subject<ActivePersonaModel?>();

        _activePersonaId = new PublicIdentityId(Guid.NewGuid());
        _testPersonaModel = new ActivePersonaModel(_activePersonaId, "Alice", 5124);

        _currentPersonaServiceMock.Setup(s => s.CurrentPersona).Returns(_testPersonaModel);
        _currentPersonaServiceMock.Setup(s => s.CurrentPersonaChanged).Returns(_currentPersonaChanged);

        var fakeRelayList = new ObservableList<RelayChannelModel>();
        var fakeRelayView = fakeRelayList.CreateView(r => r);
        _relayStateServiceMock.Setup(r => r.Relays).Returns(fakeRelayView);

        _transportsSidebar = new TransportsSidebarViewModel(
            new ShellNavigationStateService(NullLogger<ShellNavigationStateService>.Instance),
            _relayStateServiceMock.Object,
            _fakeInboundService,
            _currentPersonaServiceMock.Object,
            _windowManagerMock.Object,
            NullLogger<TransportsSidebarViewModel>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        SynchronizationContext.SetSynchronizationContext(_previousSyncContext);
        _currentPersonaChanged.Dispose();
        _transportsSidebar.Dispose();
        _fakeInboundService.Dispose();
    }

    private MainWindowViewModel CreateViewModel()
    {
        return new MainWindowViewModel(
            _currentPersonaServiceMock.Object,
            _transportsSidebar);
    }

    [Test]
    public void Constructor_WithNullDependencies_ThrowsArgumentNullException()
    {
        var act1 = () => new MainWindowViewModel(null!, _transportsSidebar);
        act1.Should().Throw<ArgumentNullException>().WithParameterName("currentPersonaService");

        var act2 = () => new MainWindowViewModel(_currentPersonaServiceMock.Object, null!);
        act2.Should().Throw<ArgumentNullException>().WithParameterName("transportsSidebar");
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
    public void CurrentPersonaChanged_UpdatesViewModel()
    {
        using var vm = CreateViewModel();

        var newPersonaId = new PublicIdentityId(Guid.NewGuid());
        var newPersona = new ActivePersonaModel(newPersonaId, "Bob", 5126);

        _currentPersonaChanged.OnNext(newPersona);

        vm.CurrentPersona.Should().Be(newPersona);
        vm.HasCurrentPersona.Should().BeTrue();
    }

    [Test]
    public void ToggleInspectorCommand_TogglesIsInspectorOpen()
    {
        using var vm = CreateViewModel();
        vm.IsInspectorOpen.Should().BeFalse();

        vm.ToggleInspectorCommand.Execute(null);
        vm.IsInspectorOpen.Should().BeTrue();

        vm.ToggleInspectorCommand.Execute(null);
        vm.IsInspectorOpen.Should().BeFalse();
    }

    [Test]
    public void ToggleChatsSidebarCommand_TogglesIsChatsSidebarOpen()
    {
        using var vm = CreateViewModel();
        vm.IsChatsSidebarOpen.Should().BeTrue();

        vm.ToggleChatsSidebarCommand.Execute(null);
        vm.IsChatsSidebarOpen.Should().BeFalse();

        vm.ToggleChatsSidebarCommand.Execute(null);
        vm.IsChatsSidebarOpen.Should().BeTrue();
    }
}
