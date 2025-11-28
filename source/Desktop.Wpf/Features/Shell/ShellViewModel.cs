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

namespace Desktop.Wpf.Features.Shell;

public sealed class ShellViewModel : ViewModelBase
{
    public IReadOnlyBindableReactiveProperty<object?> CurrentView { get; }
    public BindableReactiveProperty<bool> IsLoading { get; }

    private readonly INavigationService _navigation;
    private readonly Percolator.Application.Identity.ISelfIdentityRepository _repo;
    private readonly SelfIdentity _self;
    private readonly SessionsSidebarViewModel _sessionsVm;
    private readonly IServiceProvider _services;

    public ShellViewModel(INavigationService navigation,
                          Percolator.Application.Identity.ISelfIdentityRepository repo,
                          SelfIdentity self,
                          SessionsSidebarViewModel sessionsVm,
                          IServiceProvider services)
    {
        _navigation = navigation;
        _repo = repo;
        _self = self;
        _sessionsVm = sessionsVm;
        _services = services;

        // Bind navigation stream to a bindable read-only property for ContentControl binding later
        CurrentView = navigation.ViewStream
            .ObserveOnCurrentSynchronizationContext()
            .ToReadOnlyBindableReactiveProperty<object?>(null);

        IsLoading = new BindableReactiveProperty<bool>(true);

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

            _navigation.Navigate(null);
        }
        finally
        {
            IsLoading.Value = false;
        }
    }

    protected override void DisposeCore()
    {
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
