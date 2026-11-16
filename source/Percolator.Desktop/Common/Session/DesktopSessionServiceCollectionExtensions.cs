namespace Percolator.Desktop.Common.Session;

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Apps.Chat.DependencyInjection;
using Percolator.Apps.FileTransfer;
using Percolator.Apps.FileTransfer.Features.IngressControl;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Storage;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Shell.ViewModels;
using Percolator.Desktop.Features.Startup.ViewModels;
using Percolator.Desktop.Features.Transports.Services;
using Percolator.Desktop.Features.Transports.ViewModels;
using Percolator.PluginSdk;

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
        services.AddTransient<AddConnectionDialogViewModel>();
        services.AddTransient<PendingRequestsPanelViewModel>();
        services.AddTransient<ShareInvitationDialogViewModel>();
        services.AddSingleton<IQrCodeGenerator, QrCodeGenerator>();

        // Outbound pending connection retry and state services (CONN-08)
        services.AddSingleton<OutboundPendingRetryWorker>();
        services.AddSingleton<IOutboundPendingRetryWorker>(sp => sp.GetRequiredService<OutboundPendingRetryWorker>());
        services.AddHostedService(sp => sp.GetRequiredService<OutboundPendingRetryWorker>());
        services.AddSingleton<IPeerConnectionStateService, PeerConnectionStateService>();

        // App-payload dispatch surface for the mailbox drain (DRAIN-06): without these,
        // the shared IAppRouter registers no handlers and drained payloads die as
        // unhandled terminal rejections. AddChatApp is verbatim-safe (all lifetimes
        // consistent); file transfer is composed manually because AddFileTransferApp
        // registers FileTransferPayloadHandler/FileTransferIngressFilter as singletons
        // with scoped repo deps (captive under this host's ValidateScopes), and the
        // filter's IsEnabled default of false would discard every FT payload.
        services.AddChatApp();
        services.AddSingleton<IAppPlugin, FileTransferPlugin>();
        services.AddScoped<IAppPayloadHandler, FileTransferPayloadHandler>();
        // Null Object default from AddFileTransferApp — pool connection is a session
        // UX concern, not required for mailbox ingress.
        services.AddSingleton<ITransferStreamPool>(NullTransferStreamPool.Instance);

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
        services.AddSingleton(_ => stage1.GetRequiredService<IClipboardService>());
        services.AddSingleton(_ => stage1.GetRequiredService<TimeProvider>());

        return services;
    }
}
