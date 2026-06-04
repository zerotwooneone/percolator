using R3;
using Desktop.Wpf.Shared.Navigation;
using Desktop.Wpf.Features.Self;
using Desktop.Wpf.Features.Sessions;
using Desktop.Wpf.Shared.Mvvm;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.Identity;
using Percolator.Identity;
using System.Windows.Input;
using Desktop.Wpf.Shared.Windowing;

namespace Desktop.Wpf.Features.Shell;

public sealed class ShellViewModel : ViewModelBase
{
    public IReadOnlyBindableReactiveProperty<object?> CurrentView { get; }
    public BindableReactiveProperty<bool> IsLoading { get; }

    private readonly INavigationService _navigation;
    private readonly IIdentityBootstrap _identityBootstrap;
    private IServiceScope? _identityScope;
    private readonly IIdentityScopeAccessor _identityScopeAccessor;
    private readonly IWindowManager _windowManager;

    public ICommand OpenHandshakeSimulatorCommand { get; }

    public ShellViewModel(INavigationService navigation,
                          IIdentityBootstrap identityBootstrap,
                          IIdentityScopeAccessor identityScopeAccessor,
                          IWindowManager windowManager)
    {
        _navigation = navigation;
        _identityBootstrap = identityBootstrap;
        _identityScopeAccessor = identityScopeAccessor;
        _windowManager = windowManager;

        if (_identityScopeAccessor.Current is null)
        {
            throw new InvalidOperationException("Identity scope not available");
        }

        // Bind navigation stream to a bindable read-only property for ContentControl binding later
        CurrentView = navigation.ViewStream
            .ObserveOnCurrentSynchronizationContext()
            .ToReadOnlyBindableReactiveProperty<object?>();

        IsLoading = new BindableReactiveProperty<bool>(true);

        OpenHandshakeSimulatorCommand = new AsyncRelayCommand(async _ =>
        {
            // Only show when identity scope is available
            if (_identityScopeAccessor.Current is null) throw new InvalidOperationException("Identity scope not available");
            _windowManager.ShowFor<Desktop.Wpf.Features.Simulator.HandshakeSimulatorHostViewModel>();
            await Task.CompletedTask;
        });

        // Show loading screen first
        _navigation.Navigate(new LoadingViewModel());

        // Kick off startup after construction
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            // Bootstrap the identity using the IdentityStateService
            await _identityBootstrap.BootstrapAsync(CancellationToken.None);

            // Build the SessionShell from the identity-scoped provider
            var sidebarVm = _identityScopeAccessor.Current.GetRequiredService<SessionsSidebarViewModel>();
            var sessionShell = _identityScopeAccessor.Current.GetRequiredService<Desktop.Wpf.Features.Sessions.SessionShellViewModel>();
            sessionShell.Sidebar = sidebarVm;
            sessionShell.RightPane = _identityScopeAccessor.Current.GetRequiredService<Desktop.Wpf.Features.Sessions.SelectedChannelPaneViewModel>();
            _navigation.Navigate(sessionShell);
        }
        finally
        {
            IsLoading.Value = false;
        }
    }

    protected override void DisposeCore()
    {
        _identityScopeAccessor.Current = null;
        Disposable.Dispose(CurrentView, IsLoading);
    }
}
