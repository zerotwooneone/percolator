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

namespace Desktop.Wpf.Features.Shell;

public sealed class ShellViewModel : ViewModelBase
{
    public IReadOnlyBindableReactiveProperty<object?> CurrentView { get; }
    public BindableReactiveProperty<bool> IsLoading { get; }

    private readonly INavigationService _navigation;
    private readonly Percolator.Application.Identity.ISelfIdentityRepository _repo;
    private readonly SelfIdentityModel _self;
    private readonly IServiceProvider _services;
    private IServiceScope? _identityScope;

    public ShellViewModel(INavigationService navigation,
                          Percolator.Application.Identity.ISelfIdentityRepository repo,
                          SelfIdentityModel self,
                          IServiceProvider services)
    {
        _navigation = navigation;
        _repo = repo;
        _self = self;
        _services = services;

        // Bind navigation stream to a bindable read-only property for ContentControl binding later
        CurrentView = navigation.ViewStream
            .ObserveOnCurrentSynchronizationContext()
            .ToReadOnlyBindableReactiveProperty<object?>();

        IsLoading = new BindableReactiveProperty<bool>(true);

        // Show loading screen first
        _navigation.Navigate(new LoadingViewModel());

        // Kick off startup after construction
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            // Simulate multi-identity: try to fetch the active one (hardcoded id=1 for now)
            var dto = await _repo.GetByIdAsync(1);
            if (dto is null)
            {
                // Navigate to new-user screen
                var newUser = _services.GetRequiredService<Desktop.Wpf.Features.Shell.NewUserView>();
                _navigation.Navigate(newUser);
                return;
            }

            // Populate SelfIdentity model
            _self.DisplayName.Value = dto.Name;
            _self.Initials.Value = ComputeInitials(dto.Name);
            _self.Id.Value = dto.Id.ToString();

            // Create a scoped DI context for identity-bound services and set ActiveIdentity
            var scopeFactory = _services.GetRequiredService<IServiceScopeFactory>();
            _identityScope?.Dispose();
            _identityScope = scopeFactory.CreateScope();
            var scope = _identityScope;
            var mutator = scope.ServiceProvider.GetService<IActiveIdentityMutator>();
            if (mutator is not null)
            {
                var identityRecord = new IdentityRecord(dto.PeerId, dto.Name)
                {
                    SelfIdentityId = dto.Id
                };
                using var eph = ECDiffieHellman.Create();
                using var eph2 = ECDiffieHellman.Create();
                var keys = new X3dhKeys(eph, eph2);
                mutator.SetActiveIdentity(identityRecord, keys);
            }

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
