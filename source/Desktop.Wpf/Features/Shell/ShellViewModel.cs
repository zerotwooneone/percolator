using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using R3;
using Desktop.Wpf.Shared.Navigation;
using Desktop.Wpf.Features.Self;
using Desktop.Wpf.Features.Sessions;
using Desktop.Wpf.Shared.Mvvm;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.Identity;
using Percolator.Identity;
using Percolator.Identity.Model;
using System.Security.Cryptography;
using Desktop.Wpf;
using MediatR;
using System.Windows.Input;
using Desktop.Wpf.Shared.Windowing;
using Desktop.Wpf.Shared.Mvvm;

namespace Desktop.Wpf.Features.Shell;

public sealed class ShellViewModel : ViewModelBase
{
    public IReadOnlyBindableReactiveProperty<object?> CurrentView { get; }
    public BindableReactiveProperty<bool> IsLoading { get; }

    private readonly INavigationService _navigation;
    private readonly ISelfIdentityRepository _repo;
    private readonly IStartupIdentityService _startupIdentity;
    private readonly SelfIdentityModel _self;
    private readonly IServiceProvider _services;
    private IServiceScope? _identityScope;
    private readonly IIdentityScopeAccessor _identityScopeAccessor;
    private readonly IWindowManager _windowManager;

    public ICommand OpenHandshakeSimulatorCommand { get; }

    public ShellViewModel(INavigationService navigation,
                          ISelfIdentityRepository repo,
                          IStartupIdentityService startupIdentity,
                          SelfIdentityModel self,
                          IServiceProvider services,
                          IIdentityScopeAccessor identityScopeAccessor,
                          IWindowManager windowManager)
    {
        _navigation = navigation;
        _repo = repo;
        _startupIdentity = startupIdentity;
        _self = self;
        _services = services;
        _identityScopeAccessor = identityScopeAccessor;
        _windowManager = windowManager;

        // Bind navigation stream to a bindable read-only property for ContentControl binding later
        CurrentView = navigation.ViewStream
            .ObserveOnCurrentSynchronizationContext()
            .ToReadOnlyBindableReactiveProperty<object?>();

        IsLoading = new BindableReactiveProperty<bool>(true);

        OpenHandshakeSimulatorCommand = new AsyncRelayCommand(async _ =>
        {
            // Only show when identity scope is available
            if (_identityScopeAccessor.Current is null) throw new InvalidOperationException("Identity scope not available");
            _windowManager.ShowFor<Desktop.Wpf.Features.Simulator.HandshakeSimulatorViewModel>();
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
            // Resolve or create the domain identity via startup service
            var domainIdentity = await _startupIdentity.ResolveOrCreateAsync();
            // Fetch DTO by resolved id to retrieve PeerId (until DTO usages are removed)
            var dto = await _repo.GetByIdAsync(domainIdentity.Id);
            if (dto is null)
            {
                // Navigate to new-user screen (VM-first)
                var newUserVm = _services.GetRequiredService<Desktop.Wpf.Features.Shell.NewUserViewModel>();
                _navigation.Navigate(newUserVm);
                return;
            }

            // Populate SelfIdentity model
            
            //todo: figure out what to use for display name
            var displayName = dto.DisplayName?.Value ?? dto.Id.ToString();
            _self.DisplayName.Value = displayName;
            _self.Initials.Value = ComputeInitials(displayName);
            _self.Id.Value = dto.Id.ToString();

            // Create a scoped DI context for identity-bound services and set ActiveIdentity
            var scopeFactory = _services.GetRequiredService<IServiceScopeFactory>();
            _identityScope?.Dispose();
            _identityScope = scopeFactory.CreateScope();
            var scope = _identityScope;
            var mutator = scope.ServiceProvider.GetService<IActiveIdentityMutator>();
            if (mutator is not null)
            {
                //todo: figure out what to use for peer id
                var identityRecord = new IdentityRecord(Guid.NewGuid(), displayName)
                {
                    SelfIdentityId = dto.Id
                };
                using var eph = ECDiffieHellman.Create();
                using var eph2 = ECDiffieHellman.Create();
                var keys = new X3dhKeys(eph, eph2);
                mutator.SetActiveIdentity(identityRecord, keys);
            }

            // Expose identity-scoped provider for other features (e.g., simulator window)
            _identityScopeAccessor.Current = scope.ServiceProvider;

            // Build the SessionShell from the identity-scoped provider
            var sidebarVm = scope.ServiceProvider.GetRequiredService<SessionsSidebarViewModel>();
            var sessionShell = scope.ServiceProvider.GetRequiredService<Desktop.Wpf.Features.Sessions.SessionShellViewModel>();
            sessionShell.Sidebar = sidebarVm;
            sidebarVm.SetConductor(sessionShell);
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
        _identityScope?.Dispose();
        Disposable.Dispose(CurrentView, IsLoading);
    }

    private static string ComputeInitials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
            return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();
        return (parts[0][0].ToString() + parts[^1][0].ToString()).ToUpperInvariant();
    }
}
