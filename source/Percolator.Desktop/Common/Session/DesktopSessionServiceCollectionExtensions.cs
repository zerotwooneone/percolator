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
        services.AddSingleton<Percolator.Apps.FileTransfer.Ports.INetworkEndpointDiscoveryPort, DesktopNetworkEndpointDiscoveryPort>();

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

        // Real-time messaging, volatile drafts, and LRU session scope factory (DESK-07)
        services.AddSingleton<IChatDraftStateService, ChatDraftStateService>();
        services.AddSingleton<ISessionScopeFactory, SessionScopeFactory>();
        services.AddSingleton<IChatMessageQueryService, SqlChatMessageQueryService>();

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
        // consistent); file transfer registers the session-scoped TransferSessionRuntime
        // (disposed on vault lock) and scoped FileTransferPayloadHandler while omitting
        // FileTransferIngressFilter whose IsEnabled default of false would discard ingress.
        services.AddChatApp();
        services.AddSingleton<IAppPlugin, FileTransferPlugin>();
        services.AddSingleton<TransferSessionRuntimeOptions>(_ => new TransferSessionRuntimeOptions());
        services.AddSingleton<IBackgroundPayloadDispatcher, Percolator.Apps.FileTransfer.Features.Runtime.ScopedBackgroundPayloadDispatcher>();
        services.AddSingleton<ITransferSessionRuntime, Percolator.Apps.FileTransfer.Features.Runtime.TransferSessionRuntime>();
        services.AddSingleton<FileTransferIngressFilter>(sp =>
        {
            var repo = sp.GetRequiredService<IFileTransferFilterPolicyRepository>();
            return new FileTransferIngressFilter(repo) { IsEnabled = true };
        });
        services.AddSingleton<IAppIngressFilter>(sp => sp.GetRequiredService<FileTransferIngressFilter>());
        services.AddScoped<IAppPayloadHandler, FileTransferPayloadHandler>();
        services.AddScoped<IFileTransferActionHandler, Percolator.Apps.FileTransfer.Features.TransferNegotiation.FileTransferActionHandler>();
        services.AddScoped<Percolator.Apps.FileTransfer.Features.MultiStreamEngine.TransferSessionCoordinator>();
        services.AddScoped<Percolator.Apps.FileTransfer.Features.Storage.DiskChunkAssembler>();
        services.AddScoped<Percolator.Apps.FileTransfer.Features.MultiStreamEngine.ChunkPipelineScheduler>();
        services.AddScoped<Percolator.Apps.FileTransfer.Features.SwarmCoordination.SwarmCoordinator>();
        services.AddScoped<Percolator.Apps.FileTransfer.Features.SwarmCoordination.EndgameCoordinator>();

        // Session-bound window manager: window scopes come off the SESSION root — no
        // provider parameter on Show/ShowDialogAsync and zero call-site changes (the
        // Stage-1 singleton keeps its own instance untouched).
        services.AddSingleton<IWindowManager>(sp =>
            new WindowManager(sp, sp.GetRequiredService<ILogger<WindowManager>>()));

        // Stage-1 live-instance imports — the Stage-1 deps session-bound VMs
        // ctor-inject. ILogger<> resolves free via Host.CreateDefaultBuilder;
        // ICryptoEngine/IDateTimeProvider come from the session's own core-slice
        // registrations.
        services.AddSingleton(_ => stage1.GetRequiredService<IWindowHandoffCoordinator>());
        services.AddSingleton(_ => stage1.GetRequiredService<IIdenticonGenerator>());
        services.AddSingleton(_ => stage1.GetRequiredService<Percolator.Application.Ports.IPortReservationService>());
        services.AddSingleton(_ => stage1.GetRequiredService<IClipboardService>());
        services.AddSingleton(_ => stage1.GetRequiredService<IDialogService>());
        services.AddSingleton(_ => stage1.GetRequiredService<IFilePickerService>());
        services.AddSingleton(_ => stage1.GetRequiredService<TimeProvider>());

        return services;
    }
}
