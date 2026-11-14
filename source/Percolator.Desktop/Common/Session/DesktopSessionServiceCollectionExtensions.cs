namespace Percolator.Desktop.Common.Session;

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Storage;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Shell.ViewModels;
using Percolator.Desktop.Features.Startup.ViewModels;

/// <summary>
/// Session-container registrations owned by the Desktop executable (CTX-03). Session-bound
/// facades and ViewModels ctor-inject session ports directly; the Stage-1 singletons they
/// still depend on are forwarded as live instances, not re-registrations.
/// </summary>
internal static class DesktopSessionServiceCollectionExtensions
{
    public static IServiceCollection AddDesktopSessionServices(
        this IServiceCollection services,
        IServiceProvider stage1)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(stage1);

        // Session-bound Desktop services + VMs.
        services.AddTransient<IExchangeDirectory, ExchangeDirectoryService>();
        services.AddTransient<IPersonaDirectory, PersonaDirectoryService>();
        // Vault-backed launch settings — personas and relay hosts share the one encrypted
        // identity_settings table via IVaultSettingsStore (STOR-03).
        services.AddSingleton<IDesktopSettingsRepository, VaultDesktopSettingsRepository>();
        // WIN-02: shell geometry rides the same store — JSON in the vault's app_settings.
        services.AddSingleton<IShellWindowGeometryStore, VaultShellWindowGeometryStore>();
        services.AddTransient<IdentityGateViewModel>();
        services.AddTransient<MainWindowViewModel>();     // resolved at the WIN-02 seam
        services.AddTransient<ExchangeManageViewModel>();
        services.AddTransient<ExchangeCreationViewModel>();
        services.AddTransient<CreatePersonaDialogViewModel>();
        services.AddTransient<DeletePersonaDialogViewModel>();
        services.AddTransient<StopUsingExchangeDialogViewModel>();

        // Session-bound window manager: window scopes come off the SESSION root — no
        // provider parameter on Show/ShowDialogAsync and zero call-site changes (the
        // Stage-1 singleton keeps its own instance untouched).
        services.AddSingleton<IWindowManager>(sp =>
            new WindowManager(sp, sp.GetRequiredService<ILogger<WindowManager>>()));

        // Stage-1 live-instance imports — the only Stage-1 deps any session-bound VM
        // ctor-injects. ILogger<> resolves free via Host.CreateDefaultBuilder;
        // ICryptoEngine/IDateTimeProvider come from the session's own core-slice
        // registrations; IDialogService/ILauncherService need no import (their only
        // consumers are Stage-1 VMs).
        services.AddSingleton(_ => stage1.GetRequiredService<IWindowHandoffCoordinator>());
        services.AddSingleton(_ => stage1.GetRequiredService<IIdenticonGenerator>());
        // Desktop socket-probe port — not the Application-layer port reservation service.
        services.AddSingleton(_ => stage1.GetRequiredService<IPortReservationService>());

        return services;
    }
}
