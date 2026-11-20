namespace Percolator.Desktop.Common.Session;

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Application.Connections;
using Percolator.Application.Delivery.Ports;
using Percolator.Application.Ports;
using Percolator.Apps.Chat.DependencyInjection;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Apps.FileTransfer;
using Percolator.Apps.FileTransfer.Features.IngressControl;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Desktop.Common.Navigation;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Storage;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Chats.Services;
using Percolator.Desktop.Features.Chats.ViewModels;
using Percolator.Desktop.Features.Shell.ViewModels;
using Percolator.Desktop.Features.Startup.ViewModels;
using Percolator.Desktop.Features.Transports.Services;
using Percolator.Desktop.Features.Transports.ViewModels;
using Percolator.Infrastructure.Persistence.Queries;
using Percolator.Infrastructure.Persistence.Repositories;
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
        // Dialog ViewModels (CreatePersonaDialogViewModel, DeletePersonaDialogViewModel, etc.)
        // are instantiated on-demand inside scoped dialog lifetimes via IWindowManager (ActivatorUtilities + IDialogContext).
        services.AddSingleton<IQrCodeGenerator, QrCodeGenerator>();
        services.AddSingleton<ICurrentPersonaService, CurrentPersonaService>();

        // Navigation and Left Rail state services (DESK-05)
        services.AddSingleton<IShellNavigationStateService, ShellNavigationStateService>();
        services.AddSingleton<IRelayStateService, RelayStateService>();
        services.AddSingleton<IInboundRequestStateService, InboundRequestStateService>();
        services.AddTransient<TransportsSidebarViewModel>();

        // Outbound pending connection retry and state services (CONN-08)
        services.AddSingleton<IPeerConnectionService, DesktopPeerConnectionService>();
        services.AddSingleton<OutboundPendingRetryWorker>();
        services.AddSingleton<IOutboundPendingRetryWorker>(sp => sp.GetRequiredService<OutboundPendingRetryWorker>());
        services.AddHostedService(sp => sp.GetRequiredService<OutboundPendingRetryWorker>());
        // Conversation state and query services (DESK-06) - thread-safe singleton over IDbContextFactory
        services.AddSingleton<IConversationListQueryService, SqlConversationListQueryService>();
        services.AddSingleton<IConversationStateService, ConversationStateService>();
        services.AddTransient<ChatsSidebarViewModel>();
        services.AddScoped<IOutboundConnectionWorkflow, OutboundConnectionWorkflow>();
        services.AddScoped<InboundConnectionWorkflow>();
        services.AddSingleton<IInboundConnectionWorkflow, DesktopInboundConnectionWorkflow>();

        // Inbound contact query service (CONN-11) - thread-safe singleton over IDbContextFactory
        services.AddSingleton<IPeerContactQueryService, SqlPeerContactQueryService>();

        // Persona relay query service (CONN-12d) - thread-safe singleton over IDbContextFactory
        services.AddSingleton<IPersonaRelayQueryService, SqlPersonaRelayQueryService>();

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
        services.AddSingleton(_ => stage1.GetRequiredService<Percolator.Application.Ports.IPortReservationService>());
        services.AddSingleton(_ => stage1.GetRequiredService<IClipboardService>());
        services.AddSingleton(_ => stage1.GetRequiredService<TimeProvider>());

        return services;
    }
}
